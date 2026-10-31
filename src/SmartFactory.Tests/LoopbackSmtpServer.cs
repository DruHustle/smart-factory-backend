using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartFactory.Tests;

// The SMTP client exposes certificate validation through a process-wide callback.
// Serialize this collection, pin exactly this fixture certificate, and restore
// the previous callback on disposal. Production TLS configuration is untouched.
[CollectionDefinition("SMTP transport", DisableParallelization = true)]
public sealed class SmtpTransportCollection { }

internal sealed class LoopbackSmtpServer : IAsyncDisposable
{
    public const string Username = "synthetic-smtp-user";
    public const string Password = "synthetic-smtp-password";
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly X509Certificate2 certificate;
    private readonly RemoteCertificateValidationCallback? previousValidation;
    private readonly TaskCompletionSource<CapturedMail> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task serving;
    public int Port { get; }
    public Task<CapturedMail> Message => received.Task;

    public LoopbackSmtpServer()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var expected = certificate.GetCertHashString(HashAlgorithmName.SHA256);
#pragma warning disable SYSLIB0014 // SmtpClient has no per-instance certificate callback.
        previousValidation = ServicePointManager.ServerCertificateValidationCallback;
        ServicePointManager.ServerCertificateValidationCallback = (_, presented, _, _) =>
            presented?.GetCertHashString(HashAlgorithmName.SHA256) == expected;
#pragma warning restore SYSLIB0014
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        serving = Serve();
    }

    private static (StreamReader Reader, StreamWriter Writer) Streams(Stream stream) =>
        (new(stream, Encoding.ASCII, false, 4096, leaveOpen: true),
         new(stream, Encoding.ASCII, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" });

    private async Task Serve()
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            await using var network = client.GetStream();
            SslStream? secured = null;
            var (reader, writer) = Streams(network);
            var tls = false;
            var authenticated = false;
            var recipient = "";
            try
            {
                await writer.WriteLineAsync("220 localhost synthetic SMTP capture");
                while (!stop.IsCancellationRequested)
                {
                    var command = await reader.ReadLineAsync(stop.Token);
                    if (command is null) return;
                    if (command.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync(tls ? "250-AUTH LOGIN" : "250-STARTTLS");
                        await writer.WriteLineAsync("250 8BITMIME");
                    }
                    else if (command.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase) && !tls)
                    {
                        await writer.WriteLineAsync("220 Start TLS");
                        reader.Dispose();
                        await writer.DisposeAsync();
                        secured = new SslStream(network, leaveInnerStreamOpen: true);
                        await secured.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                            ServerCertificate = certificate,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        }, stop.Token);
                        (reader, writer) = Streams(secured);
                        tls = true;
                    }
                    else if (command.StartsWith("AUTH LOGIN", StringComparison.OrdinalIgnoreCase) && tls)
                    {
                        var initial = command.Split(' ', 3);
                        string? login;
                        if (initial.Length == 3) login = initial[2];
                        else
                        {
                            await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                            login = await reader.ReadLineAsync(stop.Token);
                        }
                        await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                        var password = await reader.ReadLineAsync(stop.Token);
                        authenticated = Decode(login) == Username && Decode(password) == Password;
                        await writer.WriteLineAsync(authenticated ? "235 Authenticated" : "535 Invalid credentials");
                    }
                    else if (command.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase) && authenticated)
                        await writer.WriteLineAsync("250 Sender accepted");
                    else if (command.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase) && authenticated)
                    {
                        recipient = command[8..].Trim().Trim('<', '>');
                        await writer.WriteLineAsync("250 Recipient accepted");
                    }
                    else if (command.Equals("DATA", StringComparison.OrdinalIgnoreCase) && authenticated && recipient.Length > 0)
                    {
                        await writer.WriteLineAsync("354 End with a dot");
                        var content = new StringBuilder();
                        while (true)
                        {
                            var line = await reader.ReadLineAsync(stop.Token) ?? throw new IOException("SMTP fixture data ended early");
                            if (line == ".") break;
                            content.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append("\r\n");
                        }
                        await writer.WriteLineAsync("250 Captured locally");
                        received.TrySetResult(new(recipient, content.ToString(), tls, authenticated));
                    }
                    else if (command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("221 Goodbye");
                        return;
                    }
                    else throw new IOException("Unexpected SMTP fixture command");
                }
            }
            finally
            {
                reader.Dispose();
                await writer.DisposeAsync();
                if (secured is not null) await secured.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            received.TrySetException(error);
            throw;
        }
    }

    private static string Decode(string? value) => Encoding.ASCII.GetString(Convert.FromBase64String(value ?? ""));

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try { await serving; }
        finally
        {
#pragma warning disable SYSLIB0014 // Restore the exact callback used by SmtpClient.
            ServicePointManager.ServerCertificateValidationCallback = previousValidation;
#pragma warning restore SYSLIB0014
            certificate.Dispose();
            stop.Dispose();
        }
    }

    internal sealed record CapturedMail(string Recipient, string Content, bool Tls, bool Authenticated)
    {
        public string DecodedContent => Regex.Replace(Content.Replace("=\r\n", "", StringComparison.Ordinal),
            "=([A-Fa-f0-9]{2})", match => ((char)Convert.ToByte(match.Groups[1].Value, 16)).ToString());
    }
}
