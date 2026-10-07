import http from 'node:http';
import {createHash} from 'node:crypto';
import {createClient} from '@supabase/supabase-js';
import validation from './validation.cjs';
const {validateReport}=validation;
if(!process.env.SUPABASE_URL||!process.env.SUPABASE_SECRET_KEY)throw Error('Backend Supabase environment required');
const db=createClient(process.env.SUPABASE_URL,process.env.SUPABASE_SECRET_KEY,{auth:{persistSession:false,autoRefreshToken:false}});
const server=http.createServer(async(req,res)=>{
 const reply=(status,body)=>{res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(body));};
 if(req.method==='GET'&&req.url==='/health')return reply(200,{ok:true});
 if(req.method!=='POST'||req.url!=='/ingest')return reply(404,{error:'Not found'});
 const auth=req.headers.authorization||'';
 if(!/^Bearer [A-Za-z0-9_-]{43}$/.test(auth)){req.resume();return reply(401,{error:'Invalid server credential'});}
 if(!req.headers['content-type']?.startsWith('application/json')){req.resume();return reply(415,{error:'JSON required'});}
 if(Number(req.headers['content-length'])>1048576){req.resume();return reply(413,{error:'Report too large'});}
 try{
  const hash=createHash('sha256').update(auth.slice(7)).digest('hex');
  const credential=await db.from('ci_statistics_servers').select('id').eq('token_hash',hash).eq('enabled',true).maybeSingle();
  if(credential.error){req.resume();return reply(503,{error:'Statistics unavailable'});}if(!credential.data){req.resume();return reply(401,{error:'Invalid server credential'});}
  const chunks=[];let length=0;for await(const chunk of req){length+=chunk.length;if(length>1048576)return reply(413,{error:'Report too large'});chunks.push(chunk);}
  let report;try{report=validateReport(JSON.parse(Buffer.concat(chunks).toString('utf8')));}catch{return reply(400,{error:'Invalid round report'});}
  const {data,error}=await db.rpc('ingest_ci_statistics',{p_token_hash:hash,p_report:report});
  if(error)return reply(503,{error:'Unable to save round'});
  const codes={unauthorized:401,conflict:409,invalid:422,rate_limited:429};return reply(codes[data?.status]||200,data);
 }catch{return reply(503,{error:'Statistics unavailable'});}
});
server.requestTimeout=30000;server.headersTimeout=15000;server.keepAliveTimeout=5000;
server.listen(Number(process.env.PORT||3100),process.env.HOST||'127.0.0.1',()=>console.log('Statistics backend listening; place behind HTTPS reverse proxy.'));
