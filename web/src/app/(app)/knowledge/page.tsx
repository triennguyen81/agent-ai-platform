import { redirect } from "next/navigation";

// V2-F compatibility bookmark. Department Knowledge has one operational UI.
export default function Page() { redirect("/department/knowledge"); }
