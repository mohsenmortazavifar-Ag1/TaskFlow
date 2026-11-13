using Microsoft.Data.SqlClient;
using TaskFlow.Worker.Models;
using TaskFlow.Worker.Services;

namespace TaskFlow.Worker.Data;

public sealed class TenantSqlConnectionFactory(ITenantSecretDecryptor decryptor)
{
    public async Task<SqlConnection> OpenAsync(TenantDatabase tenant, CancellationToken ct)
    {
        var password = decryptor.Decrypt(tenant.SqlPasswordEncrypted);
        
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = tenant.ServerName,
            InitialCatalog = tenant.DatabaseName,
            UserID = tenant.SqlLoginName,
            Password = password,
            IntegratedSecurity = false,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            ApplicationName = "TaskFlow.OutboxDispatcher",
            PersistSecurityInfo = false,
            Pooling = true
        };

        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
