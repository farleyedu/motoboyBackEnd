// Gera uma vez. O arquivo de saída fica em obj (ignorado pelo Git).
const { createECDH } = require('node:crypto'), fs = require('node:fs'), path = require('node:path');
const subject = process.argv[2];
if (!subject || !/^https:\/\//.test(subject)) throw new Error('Informe a URL HTTPS pública do ZippyGo ou da API.');
const file = path.resolve(__dirname, '../obj/web-push-keys.env');
if (fs.existsSync(file)) throw new Error('As chaves já foram geradas. Reutilize o arquivo existente.');
const curve = createECDH('prime256v1'); curve.generateKeys();
fs.mkdirSync(path.dirname(file), { recursive: true });
fs.writeFileSync(file, 'Delivery__WebPush__Subject=' + subject + '\nDelivery__WebPush__PublicKey=' + curve.getPublicKey().toString('base64url') + '\nDelivery__WebPush__PrivateKey=' + curve.getPrivateKey().toString('base64url') + '\n', { flag: 'wx', mode: 0o600 });
process.stdout.write('Chaves salvas em obj/web-push-keys.env. Cadastre as três variáveis no backend; a chave privada permanece no servidor.\n');
