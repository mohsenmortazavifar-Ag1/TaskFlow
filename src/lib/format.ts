export const faNumber=(v:number)=>new Intl.NumberFormat("fa-IR").format(v);
export const faDateTime=(v:string|Date|null)=>v?new Intl.DateTimeFormat("fa-IR",{dateStyle:"medium",timeStyle:"short",timeZone:"Asia/Tehran"}).format(new Date(v)):"—";
export const statusLabel:Record<number,string>={0:"در انتظار",1:"در حال پردازش",2:"موفق",3:"تلاش مجدد",4:"ناموفق نهایی",5:"لغو شده"};
