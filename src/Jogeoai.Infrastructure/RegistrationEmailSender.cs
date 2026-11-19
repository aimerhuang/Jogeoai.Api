using Jogeoai.Application;
using Jogeoai.Domain;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Jogeoai.Infrastructure;

/// <summary>163 SMTP 配置；Password 为邮箱 SMTP 授权码。</summary>
public sealed class RegistrationEmailOptions
{
    public string Host { get; set; } = "smtp.163.com";
    public int Port { get; set; } = 465;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromName { get; set; } = "Jogeo AI";
}

/// <summary>通过 SMTP 隐式 TLS 发送验证邮件；不记录验证码、授权码或协议内容。</summary>
public sealed class RegistrationEmailSender(IOptions<RegistrationEmailOptions> options) : IRegistrationEmailSender
{
    /// <inheritdoc />
    public void EnsureConfigured()
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port != 465
            || !MailboxAddress.TryParse(settings.Username, out _)
            || string.IsNullOrWhiteSpace(settings.Password))
            throw new CollectorStoreException("EMAIL_NOT_CONFIGURED", "验证邮件服务尚未配置，请联系管理员。", 503);
    }

    /// <inheritdoc />
    public async Task SendCodeAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var settings = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.Username));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = "Jogeo AI 注册邮箱验证码";
        message.Body = new TextPart("plain")
        {
            Text = $"你的 Jogeo AI 注册验证码是：{code}\n\n验证码 10 分钟内有效，仅可使用一次。请勿将验证码转发给他人。\n如果你没有发起注册，请忽略此邮件。"
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(settings.Host, settings.Port, SecureSocketOptions.SslOnConnect, timeout.Token);
            await client.AuthenticateAsync(settings.Username, settings.Password, timeout.Token);
            await client.SendAsync(message, timeout.Token);
            // 邮件已被服务端接受；QUIT 失败不能把已发送邮件误报为失败。
            try { await client.DisconnectAsync(true, timeout.Token); }
            catch { /* Dispose 负责关闭连接。 */ }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CollectorStoreException("EMAIL_SEND_FAILED", "验证邮件发送失败，请稍后重试或联系管理员。", 503);
        }
    }
}
