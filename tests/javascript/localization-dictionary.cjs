const fs = require('node:fs');
const path = require('node:path');
const decode = value => value.replace(/&quot;/g, '"').replace(/&apos;/g, "'")
  .replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
module.exports = language => {
  const source = fs.readFileSync(path.join(__dirname, `../../src/WarehouseEPI.Web/Resources/Localization/ClientTexts${language === 'en' ? '.en' : ''}.resx`), 'utf8');
  return Object.fromEntries([...source.matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)]
    .map(match => [decode(match[1]), decode(match[2])]));
};
