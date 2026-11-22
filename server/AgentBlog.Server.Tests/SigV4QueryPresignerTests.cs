using AgentBlog.Storage;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// The SigV4 query presigner behind every bucket upload and download (docs/specs/media.md), lifted from Relay, which lifted it from
/// EchoOfDreams. Pinned against oracles outside this code: AWS's own published presigned-URL example
/// (https://docs.aws.amazon.com/AmazonS3/latest/API/sigv4-query-string-auth.html), and a PUT computed by an independent
/// implementation that reproduces that example (EoD's test vector). The bucket stub in <see cref="FakeBucket"/> does not
/// check signatures; these do.
/// </summary>
public sealed class SigV4QueryPresignerTests
{
    [Fact]
    public void AwssPublishedExample_ProducesItsPublishedUrl()
    {
        var url = SigV4QueryPresigner.Presign(
            "GET", new Uri("https://examplebucket.s3.amazonaws.com"), "/test.txt", "us-east-1", "s3",
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc), 86400, new Dictionary<string, string>());

        Assert.Equal(
            "https://examplebucket.s3.amazonaws.com/test.txt" +
            "?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
            "&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request" +
            "&X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host" +
            "&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404",
            url);
    }

    [Fact]
    public void AwssPublishedExample_IsWhatTheVirtualHostedAddressOfItsBucketAndKeySigns()
    {
        // AWS's example is virtual-hosted: bucket examplebucket at the endpoint s3.amazonaws.com, key test.txt.
        var url = SigV4QueryPresigner.Presign(
            "GET", SigV4QueryPresigner.VirtualHostedOrigin(new Uri("https://s3.amazonaws.com"), "examplebucket"),
            SigV4QueryPresigner.KeyUri("test.txt"), "us-east-1", "s3",
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc), 86400, new Dictionary<string, string>());

        Assert.Equal(
            "https://examplebucket.s3.amazonaws.com/test.txt" +
            "?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
            "&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request" +
            "&X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host" +
            "&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404",
            url);
    }

    private static string Put(long length = 1_048_576, string type = "image/png") =>
        SigV4QueryPresigner.Presign(
            "PUT", new Uri("https://acct.r2.cloudflarestorage.com"),
            "/eodprivate/incoming/7/0/0123456789abcdef0123456789abcdef", "auto", "s3",
            "AKIDEXAMPLE", "SECRETEXAMPLE", new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc), 900,
            new Dictionary<string, string> { ["content-length"] = length.ToString(), ["content-type"] = type });

    [Fact]
    public void AnUploadPut_SignsTheLengthTheTypeAndTheHost_ToTheIndependentlyComputedSignature()
    {
        Assert.Equal(
            "https://acct.r2.cloudflarestorage.com/eodprivate/incoming/7/0/0123456789abcdef0123456789abcdef" +
            "?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
            "&X-Amz-Credential=AKIDEXAMPLE%2F20261009%2Fauto%2Fs3%2Faws4_request" +
            "&X-Amz-Date=20261009T120000Z&X-Amz-Expires=900" +
            "&X-Amz-SignedHeaders=content-length%3Bcontent-type%3Bhost" +
            "&X-Amz-Signature=7d8f179065bd5e633a534eb4441531c11e84abf511261dd38844a0fe78044a40",
            Put());
    }

    [Fact]
    public void AnotherLengthOrType_SignsDifferently_SoTheBucketRefusesABodyOfAnotherSize()
    {
        Assert.NotEqual(Put(), Put(length: 1_048_577));
        Assert.NotEqual(Put(), Put(type: "image/jpeg"));
    }
}
