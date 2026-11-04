import {timingSafeEqual} from "node:crypto";
export function hasValidApiKey(req:Request){const expected=process.env.TASKFLOW_API_KEY;const supplied=req.headers.get("x-taskflow-api-key");if(!expected||!supplied)return false;const a=Buffer.from(expected),b=Buffer.from(supplied);return a.length===b.length&&timingSafeEqual(a,b);}
