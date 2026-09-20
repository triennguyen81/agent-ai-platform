import { NextResponse } from "next/server";
import { gateway } from "@/lib/api";
import { getSession } from "@/lib/auth";
export async function DELETE(_:Request,{params}:{params:Promise<{id:string;documentId:string}>}){const token=await getSession();if(!token)return NextResponse.json({error:"unauthorized"},{status:401});const{id,documentId}=await params;const r=await gateway.deleteContractDocument(token,id,documentId);return new NextResponse(null,{status:r.status});}
