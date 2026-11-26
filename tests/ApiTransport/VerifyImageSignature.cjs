const fs = require('fs'), crypto = require('crypto');
const publicKey = crypto.createPublicKey({
  key: Buffer.concat([Buffer.from('302a300506032b6570032100', 'hex'), Buffer.from('Y2JcQAOhLwzwSDUJPNgL04nS0Tbqm7cSRc4xk0vRMic=', 'base64')]),
  format: 'der', type: 'spki'
});
for (const base of process.argv.slice(2)) {
  const originalComment = fs.readFileSync(base + '.comment', 'utf8');
  const sig = Buffer.from(JSON.parse(originalComment).signed_hash, 'base64');
  // Preserve the original JSON encoding; only remove the signature property.
  const stripped = originalComment.replace(/,\s*"signed_hash"\s*:\s*"[^"]*"/, '');
  if (stripped === originalComment) throw new Error('Missing signed_hash: ' + base);
  const rgba = fs.readFileSync(base + '.rgba');
  const rgb = Buffer.alloc(rgba.length / 4 * 3);
  const masked = Buffer.from(rgba);
  for (let i=0,j=0; i<rgba.length; i+=4,j+=3) {
    rgba.copy(rgb,j,i,i+3); masked[i+3] &= 0xfe;
  }
  const verify = pixels => crypto.verify(null, Buffer.concat([pixels,Buffer.from(stripped,'utf8')]), publicKey,sig);
  const result = { file: base, rgb: verify(rgb), maskedRgba: verify(masked) };
  console.log(JSON.stringify(result));
  if (!result.rgb && !result.maskedRgba) process.exitCode = 1;
}

