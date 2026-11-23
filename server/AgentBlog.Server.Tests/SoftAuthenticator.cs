using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentBlog.Server.Tests;

/// <summary>
/// A passkey in software, for tests: a real P-256 key that answers WebAuthn create and get requests exactly as a phone
/// would (attestation "none", ES256), so the server's verification is ASP.NET Core's own, unmocked. It keeps one
/// credential, signs for whatever origin it is told it runs on, and counts its signatures.
/// </summary>
internal sealed class SoftAuthenticator(string origin = SignInHost.PublicUrl)
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);
    /// <summary>The user handle the server gave, as the base64url bytes it travels as.</summary>
    public string? UserHandle { get; private set; }
    public string Origin { get; set; } = origin;
    private uint _counter;

    /// <summary>navigator.credentials.create: answers the creation options with a new credential.</summary>
    public JsonObject Create(JsonNode options)
    {
        var rpId = options["rp"]!["id"]!.GetValue<string>();
        UserHandle = options["user"]!["id"]!.GetValue<string>();
        var clientData = ClientData("webauthn.create", options["challenge"]!.GetValue<string>());
        var p = _key.ExportParameters(false);
        var cose = Cbor.Map((Cbor.Int(1), Cbor.Int(2)), (Cbor.Int(3), Cbor.Int(-7)), (Cbor.Int(-1), Cbor.Int(1)),
            (Cbor.Int(-2), Cbor.Bytes(p.Q.X!)), (Cbor.Int(-3), Cbor.Bytes(p.Q.Y!)));
        var credLength = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(credLength, (ushort)CredentialId.Length);
        var authData = Concat(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), [0x45], Counter(), new byte[16], credLength, CredentialId, cose);
        var attestation = Cbor.Map((Cbor.Text("fmt"), Cbor.Text("none")), (Cbor.Text("attStmt"), Cbor.Map()), (Cbor.Text("authData"), Cbor.Bytes(authData)));
        return Credential(new JsonObject
        {
            ["clientDataJSON"] = B64(clientData),
            ["attestationObject"] = B64(attestation),
            ["transports"] = new JsonArray("internal"),
        });
    }

    /// <summary>navigator.credentials.get: signs the request with this credential.</summary>
    public JsonObject Get(JsonNode options)
    {
        var rpId = options["rpId"]!.GetValue<string>();
        var clientData = ClientData("webauthn.get", options["challenge"]!.GetValue<string>());
        _counter++;
        var authData = Concat(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), [0x05], Counter());
        var signature = _key.SignData(Concat(authData, SHA256.HashData(clientData)), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Credential(new JsonObject
        {
            ["clientDataJSON"] = B64(clientData),
            ["authenticatorData"] = B64(authData),
            ["signature"] = B64(signature),
            ["userHandle"] = UserHandle,
        });
    }

    private JsonObject Credential(JsonObject response) => new()
    {
        ["id"] = B64(CredentialId),
        ["rawId"] = B64(CredentialId),
        ["type"] = "public-key",
        ["authenticatorAttachment"] = "platform",
        ["clientExtensionResults"] = new JsonObject(),
        ["response"] = response,
    };

    private byte[] ClientData(string type, string challenge) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["type"] = type, ["challenge"] = challenge, ["origin"] = Origin, ["crossOrigin"] = false,
    }.ToJsonString());

    private byte[] Counter()
    {
        var c = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, _counter);
        return c;
    }

    private static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>Just enough CBOR (RFC 8949) for an attestation object and a COSE key.</summary>
    private static class Cbor
    {
        public static byte[] Int(long v) => v >= 0 ? Head(0, (ulong)v) : Head(1, (ulong)(-1 - v));
        public static byte[] Bytes(byte[] b) => [.. Head(2, (ulong)b.Length), .. b];
        public static byte[] Text(string s) { var b = Encoding.UTF8.GetBytes(s); return [.. Head(3, (ulong)b.Length), .. b]; }
        public static byte[] Map(params (byte[] Key, byte[] Value)[] entries) =>
            [.. Head(5, (ulong)entries.Length), .. entries.SelectMany(e => e.Key.Concat(e.Value))];

        private static byte[] Head(int major, ulong n) => n switch
        {
            < 24 => [(byte)(major << 5 | (int)n)],
            <= 0xff => [(byte)(major << 5 | 24), (byte)n],
            <= 0xffff => [(byte)(major << 5 | 25), (byte)(n >> 8), (byte)n],
            _ => [(byte)(major << 5 | 26), (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n],
        };
    }
}
