import type {Metadata} from "next";import "./globals.css";
export const metadata:Metadata={title:"TaskFlow | مدیریت پردازش‌ها",description:"سامانه مرکزی مدیریت صف‌ها، پیامک‌ها و پردازش‌های پس‌زمینه"};
export default function RootLayout({children}:{children:React.ReactNode}){return <html lang="fa" dir="rtl"><body>{children}</body></html>}
