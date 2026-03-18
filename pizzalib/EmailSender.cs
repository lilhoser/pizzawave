using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;

namespace pizzalib;

public static class EmailSender
{
    public static void SendHtml(
        Settings settings,
        string fromDisplayName,
        string toEmail,
        string subject,
        string htmlBody,
        string? attachmentPath = null)
    {
        if (string.IsNullOrWhiteSpace(settings.EmailUser))
            throw new Exception("Email user not configured.");
        if (string.IsNullOrWhiteSpace(settings.EmailPassword))
            throw new Exception("Email app password not configured.");

        var sender = new MailAddress(settings.EmailUser!, fromDisplayName);
        using var smtp = new SmtpClient
        {
            Host = GetSmtpHost(settings),
            Port = 587,
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = new NetworkCredential(sender.Address, settings.EmailPassword!),
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
            throw new Exception(BuildSmtpDiagnosticMessage(settings, ex), ex);
        }
    }

    private static string GetSmtpHost(Settings settings)
    {
        return Settings.NormalizeEmailProvider(settings.EmailProvider) switch
        {
            "yahoo" => "smtp.mail.yahoo.com",
            _ => "smtp.gmail.com"
        };
    }

    private static string BuildSmtpDiagnosticMessage(Settings settings, SmtpException ex)
    {
        var sb = new StringBuilder();
        sb.Append("Email send failed. ");
        sb.Append($"Provider={Settings.NormalizeEmailProvider(settings.EmailProvider)}, ");
        sb.Append($"Host={GetSmtpHost(settings)}, Port=587, User={settings.EmailUser}. ");
        sb.Append($"SMTP status={ex.StatusCode}. ");
        sb.Append($"Message={ex.Message}");

        if (ex.InnerException != null)
            sb.Append($" | Inner={ex.InnerException.GetType().Name}: {ex.InnerException.Message}");

        return sb.ToString();
    }
}
