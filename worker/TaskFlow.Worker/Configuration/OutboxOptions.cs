namespace TaskFlow.Worker.Configuration;

public sealed class OutboxOptions
{
    public bool Enabled { get; set; }
    public int PollingIntervalSeconds { get; set; } = 5;
    public int BatchSizePerTenant { get; set; } = 10;
    public int MaxTenantConcurrency { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 300;
}

public sealed class TenantRegistryOptions
{
    public string ConnectionString { get; set; } = "";
}

public sealed class TenantPasswordEncryptionOptions
{
    public string Algorithm { get; set; } = "Aes256Gcm";

    public string Format { get; set; } = "V1NonceTagCiphertextBase64Url";

    // Preferred format shared with the TaskFlow web app: 32-byte standard Base64.
    public string KeyBase64 { get; set; } = string.Empty;

    // Temporary backward compatibility for existing deployments; migrate to KeyBase64.
    public string KeyHex { get; set; } = string.Empty;
}
