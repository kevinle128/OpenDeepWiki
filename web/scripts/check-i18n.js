async function main() {
  const [fs, path] = await Promise.all([import('node:fs'), import('node:path')]);

  const messagesDir = path.join(__dirname, '../i18n/messages');
  const baseLocale = 'en';

  function flatten(value, prefix = '', out = {}) {
    if (value === null || value === undefined) return out;

    if (typeof value !== 'object' || Array.isArray(value)) {
      if (prefix) out[prefix] = value;
      return out;
    }

    for (const [key, child] of Object.entries(value)) {
      const nextPrefix = prefix ? `${prefix}.${key}` : key;
      flatten(child, nextPrefix, out);
    }

    return out;
  }

  function readJson(filePath) {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  }

  const locales = fs
    .readdirSync(messagesDir)
    .filter((entry) => fs.statSync(path.join(messagesDir, entry)).isDirectory())
    .sort();

  const baseLocaleDir = path.join(messagesDir, baseLocale);
  const baseFiles = fs
    .readdirSync(baseLocaleDir)
    .filter((file) => file.endsWith('.json'))
    .sort();

  const baseline = new Map();
  for (const file of baseFiles) {
    baseline.set(file, flatten(readJson(path.join(baseLocaleDir, file))));
  }

  console.log(`i18n check against ${baseLocale}`);
  console.log(`locales: ${locales.join(', ')}`);
  console.log('');

  let hasIssues = false;

  for (const locale of locales) {
    const localeDir = path.join(messagesDir, locale);
    const localeFiles = new Set(
      fs.readdirSync(localeDir).filter((file) => file.endsWith('.json'))
    );

    const missingFiles = baseFiles.filter((file) => !localeFiles.has(file));
    const extraFiles = [...localeFiles].filter((file) => !baseline.has(file));

    console.log(`== ${locale} ==`);
    if (missingFiles.length === 0 && extraFiles.length === 0) {
      console.log('files: ok');
    } else {
      if (missingFiles.length) {
        hasIssues = true;
        console.log(`missing files: ${missingFiles.join(', ')}`);
      }
      if (extraFiles.length) {
        console.log(`extra files: ${extraFiles.join(', ')}`);
      }
    }

    for (const file of baseFiles) {
      if (!localeFiles.has(file)) continue;

      const baseEntries = baseline.get(file);
      const localeEntries = flatten(readJson(path.join(localeDir, file)));

      const missingKeys = Object.keys(baseEntries).filter((key) => !(key in localeEntries));
      const extraKeys = Object.keys(localeEntries).filter((key) => !(key in baseEntries));

      if (missingKeys.length || extraKeys.length) {
        hasIssues = true;
        if (missingKeys.length) {
          console.log(`  ${file} missing ${missingKeys.length} key(s)`);
          console.log(`    ${missingKeys.join(', ')}`);
        }
        if (extraKeys.length) {
          console.log(`  ${file} extra ${extraKeys.length} key(s)`);
          console.log(`    ${extraKeys.join(', ')}`);
        }
      }
    }

    console.log('');
  }

  if (hasIssues) {
    process.exitCode = 1;
  } else {
    console.log('All locale files match the English baseline.');
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
