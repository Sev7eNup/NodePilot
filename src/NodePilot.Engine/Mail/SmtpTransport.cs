using System.Net;
using System.Net.Mail;
using NodePilot.Engine.Options;

namespace NodePilot.Engine.Mail;

/// <summary>
/// The parts of sending mail that <see cref="Activities.EmailActivity"/> and
/// <see cref="Notifications.SmtpNotificationSink"/> must decide identically: how the client is
/// built and which recipients/subjects are refused outright.
/// <para>
/// Kept in one place because a fix applied to only one caller would be a silent hole: TLS is
/// on by default here, and the recipient-list rule stops an operator (or a trigger payload
/// interpolated via <c>{{…}}</c>) from BCCing an attacker onto a notification to exfiltrate
/// log contents.
/// </para>
/// <para>
/// Deliberately NOT shared: the send/await itself. The activity bounds the await with
/// <c>WaitAsync</c> because SmtpClient's own cancellation is racy and an unresolved task strands
/// the whole execution in Running; the sink is self-isolating and uses a linked token instead.
/// The two have different failure contracts, so they keep their own send paths.
/// </para>
/// </summary>
internal static class SmtpTransport
{
    /// <summary>
    /// Builds the client with TLS as configured and credentials only when both parts are present.
    /// Caller owns disposal.
    /// </summary>
    public static SmtpClient CreateClient(SmtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Default-on TLS. SmtpClient defaults to EnableSsl=false, which would send LOGIN/PLAIN
        // credentials and the whole message body in plaintext. The option lives on SmtpOptions
        // with a safe default, and SecurityHardeningWarnings warns at boot if an operator turned
        // it off while still configuring a Username.
        var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.EnableSsl };
        if (options.Username is not null && options.Password is not null)
            client.Credentials = new NetworkCredential(options.Username, options.Password);
        return client;
    }

    /// <summary>
    /// True when the recipient is a comma/semicolon-separated list. Single-recipient only — build
    /// a second step (or a second route) for fan-out.
    /// </summary>
    public static bool IsRecipientList(string recipient) => recipient.IndexOfAny([',', ';']) >= 0;

    /// <summary>
    /// True when a CR/LF in the address or subject could split headers. MailMessage already
    /// rejects these on most paths; failing early gives a clear error instead of a transport one.
    /// </summary>
    public static bool HasHeaderInjection(string recipient, string subject) =>
        recipient.IndexOfAny(['\r', '\n']) >= 0 || subject.IndexOfAny(['\r', '\n']) >= 0;
}
