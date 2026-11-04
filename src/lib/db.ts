import sql from "mssql";
const g=globalThis as typeof globalThis & {taskflowPool?:Promise<sql.ConnectionPool>};
function config():sql.config{
 const {DB_SERVER,DB_NAME,DB_USER,DB_PASSWORD}=process.env;
 if(!DB_SERVER||!DB_NAME||!DB_USER||!DB_PASSWORD)throw new Error("SQL Server environment variables are not fully configured.");
 return {server:DB_SERVER,port:Number(process.env.DB_PORT??1433),database:DB_NAME,user:DB_USER,password:DB_PASSWORD,options:{encrypt:process.env.DB_ENCRYPT==="true",trustServerCertificate:process.env.DB_TRUST_SERVER_CERTIFICATE==="true",enableArithAbort:true},pool:{max:Number(process.env.DB_POOL_MAX??10),min:Number(process.env.DB_POOL_MIN??0),idleTimeoutMillis:30000},connectionTimeout:15000,requestTimeout:30000};
}
export async function getPool(){if(!g.taskflowPool)g.taskflowPool=new sql.ConnectionPool(config()).connect().catch(e=>{g.taskflowPool=undefined;throw e;});return g.taskflowPool;}
export {sql};
