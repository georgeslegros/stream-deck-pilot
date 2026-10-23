using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Api.Security;

/// <summary>
/// ASP.NET Core Data Protection keys (#97): they sign and encrypt the session cookie, anti-forgery
/// tokens and the Blazor circuit. Kept on the data volume, under
/// <c>{Storage:BaseDirectory}/dataprotection-keys</c>, with a fixed application name, so sessions
/// survive restarts and image upgrades. Optionally encrypted at rest with a certificate
/// (<c>DataProtection:CertificatePath</c>, <c>DataProtection:CertificatePassword</c>); otherwise
/// the key files are plain and the volume must be protected like any other secret.
/// </summary>
public static class DataProtectionExtensions
{
    public const string ApplicationName = "StreamDeckPilot";

    public static string KeyDirectory(StorageOptions storage) => Path.Combine(storage.BaseDirectory, "dataprotection-keys");

    public static WebApplicationBuilder AddPersistentDataProtection(this WebApplicationBuilder builder)
    {
        var dataProtection = builder.Services.AddDataProtection().SetApplicationName(ApplicationName);

        // What PersistKeysToFileSystem does, with the directory taken from StorageOptions when the
        // options are built, so it follows the same configuration as every other store.
        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(o => o.XmlRepository = new FileSystemXmlRepository(
                new DirectoryInfo(KeyDirectory(sp.GetRequiredService<IOptions<StorageOptions>>().Value)),
                sp.GetRequiredService<ILoggerFactory>())));

        var certificatePath = builder.Configuration["DataProtection:CertificatePath"];
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, builder.Configuration["DataProtection:CertificatePassword"]);
            if (!certificate.HasPrivateKey)
                throw new InvalidOperationException($"DataProtection:CertificatePath '{certificatePath}' has no private key.");
            dataProtection.ProtectKeysWithCertificate(certificate);
        }
        return builder;
    }

    /// <summary>Says once at startup where the keys are and whether they're encrypted at rest.</summary>
    public static void LogDataProtectionMode(this WebApplication app)
    {
        var directory = KeyDirectory(app.Services.GetRequiredService<IOptions<StorageOptions>>().Value);
        if (!string.IsNullOrWhiteSpace(app.Configuration["DataProtection:CertificatePath"]))
            app.Logger.LogInformation("Data Protection keys are in {Directory}, encrypted with the configured certificate.", directory);
        else
            app.Logger.LogInformation(
                "Data Protection keys are in {Directory}, unencrypted at rest (no DataProtection:CertificatePath). Protect the data volume: anyone who can read it can forge sessions.",
                directory);
    }
}
