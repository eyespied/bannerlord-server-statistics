import {spawn} from 'node:child_process';
import assert from 'node:assert/strict';
import {once} from 'node:events';
const child=spawn(process.execPath,['server.mjs'],{cwd:import.meta.dirname,windowsHide:true,env:{...process.env,HOST:'127.0.0.1',PORT:'31131',SUPABASE_URL:'https://example.test',SUPABASE_SECRET_KEY:'test-only-not-a-real-key'},stdio:['ignore','pipe','pipe']});
try{
 await Promise.race([once(child.stdout,'data'),once(child,'exit').then(()=>{throw Error('Backend did not start');})]);
 assert.equal((await fetch('http://127.0.0.1:31131/health')).status,200);
 assert.equal((await fetch('http://127.0.0.1:31131/ingest',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).status,401);
 assert.equal((await fetch('http://127.0.0.1:31131/unknown')).status,404);
 console.log('PASS: generic backend starts, health responds and unauthenticated uploads are denied without database access');
}finally{child.kill();}
