import {NextResponse} from "next/server";import {getPool} from "@/lib/db";
export const runtime="nodejs";export const dynamic="force-dynamic";
export async function GET(){try{const p=await getPool();await p.request().query("SELECT 1 AS ok");return NextResponse.json({status:"healthy",database:"connected",timestamp:new Date().toISOString()});}catch{return NextResponse.json({status:"degraded",database:"unavailable"},{status:503});}}
