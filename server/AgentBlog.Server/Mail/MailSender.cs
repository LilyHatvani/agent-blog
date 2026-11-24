using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AgentKit.Fetch;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AgentBlog.Mail;

/// <summary>One email: who from, to whom, the subject, both bodies, and extra headers (List-Unsubscribe).</summary>
public sealed record Mail(string FromAddress, string FromName, string To, string Subject, string Html, string Text,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>What a provider did with an email.</summary>
public enum MailOutcome
{
    Sent,
    /// <summary>Try again later: a rate limit (with <see cref="MailResult.RetryAfter"/>), a server error, no answer.</summary>
    Retry,
    /// <summary>The credentials were refused: stop sending for this account until the owner fixes it.</summary>
    Auth,
    /// <summary>This email was refused (a bad address, a From the provider won't send as): don't retry it.</summary>
    Rejected,
}

public sealed record MailResult(MailOutcome Outcome, string? Message = null, TimeSpan? RetryAfter = null, string? Id = null);

/// <summary>A mail provider for one account (docs/specs/subscribe.md): Resend's API or an SMTP server.</summary>
public interface IMailSender
{
    /// <param name="idempotencyKey">The same on every retry of this email; providers that take one never send it twice.</param>
    Task<MailResult> SendAsync(Mail mail, string idempotencyKey, CancellationToken ct);
}

/// <summary>
/// Resend (<c>POST https://api.resend.com/emails</c>, a Bearer API key, an <c>Idempotency-Key</c> header; errors as
/// <c>{ statusCode, message, name }</c>; the shapes the official <c>resend</c> 6.39.0 SDK sends).
/// </summary>
public sealed class ResendSender(HttpClient http, string apiKey) : IMailSender
{
    public static readonly Uri Endpoint = new("https://api.resend.com/emails");

    public async Task<MailResult> SendAsync(Mail mail, string idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                from = Address(mail.FromName, mail.FromAddress),
                to = new[] { mail.To },
                subject = mail.Subject,
                html = mail.Html,
                text = mail.Text,
                headers = mail.Headers,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
            string? message = null, id = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                if (doc.RootElement.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String) id = i.GetString();
            }
            catch (JsonException) { }
            message = Clean(message ?? $"Resend answered {(int)response.StatusCode}.", apiKey);
            return (int)response.StatusCode switch
            {
                >= 200 and < 300 => new MailResult(MailOutcome.Sent, Id: id),
                401 or 403 => new MailResult(MailOutcome.Auth, $"Resend refused the API key: {message}"),
                429 => new MailResult(MailOutcome.Retry, $"Resend asked to wait: {message}", Wait(response)),
                409 => new MailResult(MailOutcome.Retry, $"Resend is still sending the earlier try: {message}", TimeSpan.FromMinutes(1)),
                >= 500 => new MailResult(MailOutcome.Retry, $"Resend had a server error: {message}"),
                _ => new MailResult(MailOutcome.Rejected, $"Resend refused the email: {message}"),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return GuardedHttp.Blocked(e) is { } blocked
                ? new MailResult(MailOutcome.Rejected, blocked.Message)
                : new MailResult(MailOutcome.Retry, "Resend didn't answer.");
        }
    }

    private static TimeSpan? Wait(HttpResponseMessage r) =>
        r.Headers.RetryAfter?.Delta is { } d ? d
        : r.Headers.TryGetValues("ratelimit-reset", out var v) && int.TryParse(v.FirstOrDefault(), out var s) ? TimeSpan.FromSeconds(Math.Clamp(s, 0, 86_400)) : null;

    internal static string Address(string name, string address) =>
        string.IsNullOrWhiteSpace(name) ? address : new MailboxAddress(name, address).ToString(FormatOptions.Default, true);

    internal static string Clean(string s, string secret)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (secret.Length >= 4) s = s.Replace(secret, "***", StringComparison.Ordinal);
        return s.Length <= 300 ? s : AgentBlog.Posts.XmlText.Cut(s, 299) + "…";
    }
}

/// <summary>
/// Any SMTP server, through MailKit (MIT): STARTTLS on 587 (or the port given), TLS from the start on 465, always
/// encrypted. The host must resolve to public addresses only, so an owner's setting can't reach this server's network.
/// </summary>
public sealed class SmtpSender(string host, int port, string user, string password) : IMailSender
{
    public async Task<MailResult> SendAsync(Mail mail, string idempotencyKey, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(mail.FromName, mail.FromAddress));
        message.To.Add(MailboxAddress.Parse(mail.To));
        message.Subject = mail.Subject;
        // The same id on every retry: a server or a reader's client that sees it twice knows it's one email.
        message.MessageId = $"{idempotencyKey.Replace(':', '.')}@{mail.FromAddress.Split('@').Last()}";
        foreach (var (name, value) in mail.Headers) message.Headers.Add(name, value);
        message.Body = new BodyBuilder { HtmlBody = mail.Html, TextBody = mail.Text }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0 || addresses.Any(a => !PublicAddress.IsPublic(a)))
                return new MailResult(MailOutcome.Rejected, $"{host} isn't on the public internet, so mail isn't sent through it.");
            await client.ConnectAsync(host, port, port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            if (user.Length > 0) await client.AuthenticateAsync(user, password, ct);
            var answer = await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, CancellationToken.None);
            return new MailResult(MailOutcome.Sent, Id: ResendSender.Clean(answer ?? "", password));
        }
        catch (AuthenticationException e)
        {
            return new MailResult(MailOutcome.Auth, ResendSender.Clean($"{host} refused the user or password: {e.Message}", password));
        }
        catch (SmtpCommandException e) when ((int)e.StatusCode is >= 500 and < 600)
        {
            return new MailResult(MailOutcome.Rejected, ResendSender.Clean($"{host} refused the email ({(int)e.StatusCode}): {e.Message}", password));
        }
        catch (SmtpCommandException e)
        {
            return new MailResult(MailOutcome.Retry, ResendSender.Clean($"{host} asked to try later ({(int)e.StatusCode}): {e.Message}", password));
        }
        catch (Exception e) when (e is SocketException or IOException or SmtpProtocolException or SslHandshakeException
                                   or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new MailResult(MailOutcome.Retry, ResendSender.Clean($"Couldn't reach {host}:{port}: {e.Message}", password));
        }
    }
}
