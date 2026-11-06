import {createDecipheriv} from "node:crypto";
import {sql} from "@/lib/db";

type RegistryRow = {
  TenantDatabaseId: number;
  TenantId: number;
  ServerName: string;
  DatabaseName: string;
  SqlLoginName: string;
  SqlPasswordEncrypted: string;
};

export type TenantDatabaseRef = {
  tenantDatabaseId: number;
  tenantId: number;
};

export class TenantDatabaseError extends Error {
  constructor(message: string, public readonly status: number) {
    super(message);
    this.name = "TenantDatabaseError";
  }
}

function requiredEnv(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`Environment variable ${name} is required.`);
  return value;
}

function decryptTenantPassword(value: string): string {
  const encodedKey = requiredEnv("TENANT_PASSWORD_ENCRYPTION_KEY_BASE64");
  const key = Buffer.from(encodedKey, "base64");
  if (key.length !== 32) throw new Error("Tenant password encryption key must be 32 bytes (AES-256-GCM).");
  if (!value.startsWith("v1:")) throw new Error("Unsupported tenant password envelope.");
  const envelope = Buffer.from(value.slice(3), "base64");
  if (envelope.length <= 28) throw new Error("Tenant password envelope is invalid.");

  const nonce = envelope.subarray(0, 12);
  const tag = envelope.subarray(12, 28);
  const ciphertext = envelope.subarray(28);
  const decipher = createDecipheriv("aes-256-gcm", key, nonce);
  decipher.setAuthTag(tag);
  try {
    return Buffer.concat([decipher.update(ciphertext), decipher.final()]).toString("utf8");
  } finally {
    key.fill(0);
  }
}

async function resolveTenantDatabase(tenantId: number, tenantDatabaseId?: number): Promise<RegistryRow> {
  const server = requiredEnv("MASTER_DB_SERVER");
  const user = requiredEnv("MASTER_DB_USER");
  const password = requiredEnv("MASTER_DB_PASSWORD");
  const masterPool = await new sql.ConnectionPool({
    server,
    port: Number(process.env.MASTER_DB_PORT ?? 1433),
    database: process.env.MASTER_DB_NAME ?? "MasterRepair",
    user,
    password,
    options: {
      encrypt: process.env.MASTER_DB_ENCRYPT === "true",
      trustServerCertificate: process.env.MASTER_DB_TRUST_SERVER_CERTIFICATE === "true",
      enableArithAbort: true
    },
    connectionTimeout: 15000,
    requestTimeout: 30000
  }).connect();

  try {
    const result = await masterPool.request()
      .input("tenantId", sql.BigInt, tenantId)
      .input("tenantDatabaseId", sql.BigInt, tenantDatabaseId ?? null)
      .query<RegistryRow>(`
        SELECT TOP (2) TenantDatabaseId, TenantId, ServerName, DatabaseName,
               SqlLoginName, SqlPasswordEncrypted
        FROM dbo.TenantDatabases
        WHERE TenantId = @tenantId
          AND IsActive = 1
          AND (@tenantDatabaseId IS NULL OR TenantDatabaseId = @tenantDatabaseId)
        ORDER BY TenantDatabaseId;
      `);

    if (result.recordset.length === 0)
      throw new TenantDatabaseError("برای این Tenant دیتابیس فعال پیدا نشد.", 404);
    if (result.recordset.length > 1)
      throw new TenantDatabaseError("برای این Tenant چند دیتابیس فعال وجود دارد؛ tenantDatabaseId را مشخص کنید.", 409);
    return result.recordset[0];
  } finally {
    await masterPool.close();
  }
}

export async function withTenantDatabase<T>(
  tenantId: number,
  tenantDatabaseId: number | undefined,
  action: (pool: sql.ConnectionPool, database: TenantDatabaseRef) => Promise<T>
): Promise<T> {
  const row = await resolveTenantDatabase(tenantId, tenantDatabaseId);
  const password = decryptTenantPassword(row.SqlPasswordEncrypted);
  const pool = await new sql.ConnectionPool({
    server: row.ServerName,
    database: row.DatabaseName,
    user: row.SqlLoginName,
    password,
    options: {
      encrypt: true,
      trustServerCertificate: process.env.TENANT_DB_TRUST_SERVER_CERTIFICATE === "true",
      enableArithAbort: true
    },
    connectionTimeout: 15000,
    requestTimeout: 30000,
    pool: {max: 4, min: 0, idleTimeoutMillis: 10000}
  }).connect();

  try {
    return await action(pool, {
      tenantId: row.TenantId,
      tenantDatabaseId: row.TenantDatabaseId
    });
  } finally {
    await pool.close();
  }
}
