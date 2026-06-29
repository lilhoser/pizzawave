using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;

namespace pizzad;

public static class SmtpEmailSender
{
    public static void SendHtml(
        AlertConfig config,
        string fromDisplayName,
        string toEmail,
        string subject,
        string htmlBody,
        string password,
        string? attachmentPath = null)
    {
        if (string.IsNullOrWhiteSpace(config.EmailUser))
            throw new InvalidOperationException("Email user not configured.");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Email app password not configured.");

        var sender = new MailAddress(config.EmailUser, fromDisplayName);
        using var smtp = new SmtpClient
        {
            Host = GetSmtpHost(config.EmailProvider),
            Port = 587,
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = new NetworkCredential(sender.Address, password),
            Timeout = 30000
        };

        var recipient = new MailAddress(toEmail);
        using var message = new MailMessage(sender, recipient)
        {
            Subject = subject,
            IsBodyHtml = true,
            Body = htmlBody
        };

        if (!string.IsNullOrWhiteSpace(attachmentPath) && File.Exists(attachmentPath))
        {
            var contentType = new ContentType
            {
                MediaType = MediaTypeNames.Application.Octet,
                Name = Path.GetFileName(attachmentPath)
            };
            message.Attachments.Add(new Attachment(attachmentPath, contentType));
        }

        try
        {
            smtp.Send(message);
        }
        catch (SmtpException ex)
        {
            throw new InvalidOperationException(BuildSmtpDiagnosticMessage(config, ex), ex);
        }
    }

    private static string GetSmtpHost(string provider) =>
        NormalizeEmailProvider(provider) switch
        {
            "yahoo" => "smtp.mail.yahoo.com",
            _ => "smtp.gmail.com"
        };

    private static string NormalizeEmailProvider(string provider) =>
        string.Equals(provider?.Trim(), "yahoo", StringComparison.OrdinalIgnoreCase) ? "yahoo" : "gmail";

    private static string BuildSmtpDiagnosticMessage(AlertConfig config, SmtpException ex)
    {
        var provider = NormalizeEmailProvider(config.EmailProvider);
        var sb = new StringBuilder();
        sb.Append("Email send failed. ");
        sb.Append($"Provider={provider}, ");
        sb.Append($"Host={GetSmtpHost(provider)}, Port=587, User={config.EmailUser}. ");
        sb.Append($"SMTP status={ex.StatusCode}. ");
        sb.Append($"Message={ex.Message}");
        if (ex.InnerException != null)
            sb.Append($" | Inner={ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        return sb.ToString();
    }
}
