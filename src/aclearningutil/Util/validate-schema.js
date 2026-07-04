// validate-schema.js
// Validates data files in the Learning API project:
//   A) Vocabulary files — unique id, unique enword
//   B) Sentence files   — unique id, unique ensent
//   C) Knowledge exercise files — Ajv JSON Schema validation (if folder exists)
//
// Usage (from API project root):
//   node src/aclearningutil/util/validate-schema.js

const fs = require('fs');
const path = require('path');

const STORAGE_DIR = path.join(__dirname, '..', 'Storage');
const LEARN_ENGLISH_DIR = path.join(STORAGE_DIR, 'learnenglish');
const KNOWLEDGE_DIR = path.join(STORAGE_DIR, 'knowledge-exercises');
const SCHEMA_PATH = path.join(__dirname, 'exercise-schema.json');

// ── Helpers ──────────────────────────────────────────────────────

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, 'utf8'));
}

function isPlaceholder(id) {
  return id && (String(id).startsWith('example-') || String(id).startsWith('999-'));
}

/**
 * Find duplicate values in an array of {value, index} entries.
 * Returns a Map<string, number[]> keyed by the stringified value → list of original indices.
 * Only entries with more than one occurrence are included.
 */
function findDuplicates(entries) {
  const seen = new Map();
  for (const { value, index } of entries) {
    const key = String(value).trim();
    if (!seen.has(key)) {
      seen.set(key, []);
    }
    seen.get(key).push(index);
  }
  const duplicates = new Map();
  for (const [key, indices] of seen) {
    if (indices.length > 1) {
      duplicates.set(key, indices);
    }
  }
  return duplicates;
}

// ── Section A: Vocabulary Files ──────────────────────────────────

function validateVocabularyFiles() {
  console.log('\n=== Section A: Vocabulary Files ===\n');

  const indexPath = path.join(LEARN_ENGLISH_DIR, 'words.json');
  if (!fs.existsSync(indexPath)) {
    console.log('  [SKIP] words.json index not found.');
    return { filesChecked: 0, errors: 0 };
  }

  const index = readJson(indexPath);
  let filesChecked = 0;
  let totalErrors = 0;

  for (const entry of index) {
    const filePath = path.join(LEARN_ENGLISH_DIR, entry.file);
    if (!fs.existsSync(filePath)) {
      console.log(`  ✗ ${entry.file} — file not found`);
      totalErrors++;
      filesChecked++;
      continue;
    }

    const data = readJson(filePath);
    if (!Array.isArray(data)) {
      console.log(`  ✗ ${entry.file} — not an array`);
      totalErrors++;
      filesChecked++;
      continue;
    }

    const fileErrors = [];

    // Check 1: every item must have an "id" property
    const missingIds = [];
    data.forEach((item, idx) => {
      if (item.id === undefined || item.id === null) {
        missingIds.push(idx);
      }
    });
    if (missingIds.length > 0) {
      fileErrors.push(`  Missing "id" at indices: ${missingIds.slice(0, 10).join(', ')}${missingIds.length > 10 ? ` ... and ${missingIds.length - 10} more` : ''}`);
    }

    // Check 2: all "id" values must be unique (normalized to string)
    const idEntries = data
      .map((item, idx) => ({ value: item.id, index: idx }))
      .filter(e => e.value !== undefined && e.value !== null);
    const idDupes = findDuplicates(idEntries);
    if (idDupes.size > 0) {
      for (const [val, indices] of idDupes) {
        fileErrors.push(`  Duplicate id "${val}":`);
        for (const idx of indices) {
          const item = data[idx];
          fileErrors.push(`    - id: ${item.id}, enword: "${item.enword ?? ''}"`);
        }
      }
    }

    // Check 3: all "enword" values must be unique (trimmed, case-sensitive)
    const enwordEntries = data
      .map((item, idx) => ({ value: item.enword, index: idx }))
      .filter(e => e.value !== undefined && e.value !== null);
    const enwordDupes = findDuplicates(enwordEntries);
    if (enwordDupes.size > 0) {
      for (const [val, indices] of enwordDupes) {
        fileErrors.push(`  Duplicate enword "${val}":`);
        for (const idx of indices) {
          const item = data[idx];
          fileErrors.push(`    - id: ${item.id ?? '?'}, enword: "${item.enword ?? ''}"`);
        }
      }
    }

    filesChecked++;
    if (fileErrors.length > 0) {
      console.log(`  ✗ ${entry.file} — ${fileErrors.length} error(s)`);
      for (const err of fileErrors) {
        console.log(err);
      }
      totalErrors += fileErrors.length;
    } else {
      console.log(`  ✓ ${entry.file} — OK (${data.length} items)`);
    }
  }

  return { filesChecked, errors: totalErrors };
}

// ── Section B: Sentence Files ────────────────────────────────────

function validateSentenceFiles() {
  console.log('\n=== Section B: Sentence Files ===\n');

  const indexPath = path.join(LEARN_ENGLISH_DIR, 'sentences.json');
  if (!fs.existsSync(indexPath)) {
    console.log('  [SKIP] sentences.json index not found.');
    return { filesChecked: 0, errors: 0 };
  }

  const index = readJson(indexPath);
  let filesChecked = 0;
  let totalErrors = 0;

  for (const entry of index) {
    const filePath = path.join(LEARN_ENGLISH_DIR, entry.file);
    if (!fs.existsSync(filePath)) {
      console.log(`  ✗ ${entry.file} — file not found`);
      totalErrors++;
      filesChecked++;
      continue;
    }

    const data = readJson(filePath);
    if (!Array.isArray(data)) {
      console.log(`  ✗ ${entry.file} — not an array`);
      totalErrors++;
      filesChecked++;
      continue;
    }

    const fileErrors = [];

    // Check 1: every item must have an "id" property
    const missingIds = [];
    data.forEach((item, idx) => {
      if (item.id === undefined || item.id === null) {
        missingIds.push(idx);
      }
    });
    if (missingIds.length > 0) {
      fileErrors.push(`  Missing "id" at indices: ${missingIds.slice(0, 10).join(', ')}${missingIds.length > 10 ? ` ... and ${missingIds.length - 10} more` : ''}`);
    }

    // Check 2: all "id" values must be unique (normalized to string)
    const idEntries = data
      .map((item, idx) => ({ value: item.id, index: idx }))
      .filter(e => e.value !== undefined && e.value !== null);
    const idDupes = findDuplicates(idEntries);
    if (idDupes.size > 0) {
      for (const [val, indices] of idDupes) {
        fileErrors.push(`  Duplicate id "${val}":`);
        for (const idx of indices) {
          const item = data[idx];
          const sentPreview = (item.ensent ?? '').substring(0, 60);
          fileErrors.push(`    - id: ${item.id}, ensent: "${sentPreview}${sentPreview.length >= 60 ? '...' : ''}"`);
        }
      }
    }

    // Check 3: all "ensent" values must be unique (trimmed)
    const ensentEntries = data
      .map((item, idx) => ({ value: item.ensent, index: idx }))
      .filter(e => e.value !== undefined && e.value !== null);
    const ensentDupes = findDuplicates(ensentEntries);
    if (ensentDupes.size > 0) {
      for (const [val, indices] of ensentDupes) {
        const sentPreview = val.substring(0, 60);
        fileErrors.push(`  Duplicate ensent "${sentPreview}${sentPreview.length >= 60 ? '...' : ''}":`);
        for (const idx of indices) {
          const item = data[idx];
          fileErrors.push(`    - id: ${item.id ?? '?'}, ensent: "${(item.ensent ?? '').substring(0, 60)}${(item.ensent ?? '').length >= 60 ? '...' : ''}"`);
        }
      }
    }

    filesChecked++;
    if (fileErrors.length > 0) {
      console.log(`  ✗ ${entry.file} — ${fileErrors.length} error(s)`);
      for (const err of fileErrors) {
        console.log(err);
      }
      totalErrors += fileErrors.length;
    } else {
      console.log(`  ✓ ${entry.file} — OK (${data.length} items)`);
    }
  }

  return { filesChecked, errors: totalErrors };
}

// ── Section C: Knowledge Exercise Files ──────────────────────────

function validateKnowledgeExerciseFiles() {
  console.log('\n=== Section C: Knowledge Exercise Files ===\n');

  if (!fs.existsSync(KNOWLEDGE_DIR)) {
    console.log('  [SKIP] Knowledge exercises directory not found — skipped.');
    console.log(`         Expected at: ${KNOWLEDGE_DIR}`);
    return { filesChecked: 0, errors: 0 };
  }

  const indexPath = path.join(KNOWLEDGE_DIR, 'data.json');
  if (!fs.existsSync(indexPath)) {
    console.log('  [SKIP] data.json index not found in knowledge-exercises directory.');
    return { filesChecked: 0, errors: 0 };
  }

  // Load Ajv dynamically — only needed when knowledge exercises exist
  let Ajv;
  try {
    Ajv = require('ajv/dist/2020');
  } catch {
    console.log('  [ERROR] Ajv is not installed. Run: npm install ajv');
    console.log('          in the util/ folder to enable knowledge exercise validation.');
    return { filesChecked: 0, errors: 1 };
  }

  if (!fs.existsSync(SCHEMA_PATH)) {
    console.log(`  [ERROR] Schema file not found: ${SCHEMA_PATH}`);
    return { filesChecked: 0, errors: 1 };
  }

  const ajv = new Ajv({ allErrors: true, strict: false });
  const schema = JSON.parse(fs.readFileSync(SCHEMA_PATH, 'utf8'));
  const validate = ajv.compile(schema);

  const index = readJson(indexPath);
  let filesChecked = 0;
  let totalErrors = 0;

  for (const entry of index) {
    const filePath = path.join(KNOWLEDGE_DIR, entry.file);
    if (!fs.existsSync(filePath)) {
      console.log(`  ✗ ${entry.file} — file not found`);
      totalErrors++;
      filesChecked++;
      continue;
    }

    const data = readJson(filePath);
    const valid = validate(data);

    if (valid) {
      console.log(`  ✓ ${entry.file} — schema OK`);
    } else {
      // Filter out errors from placeholder items
      const filtered = [];
      for (const err of validate.errors) {
        const parts = err.instancePath.split('/').filter(Boolean);
        let skip = false;
        if (parts.length >= 1) {
          const itemIdx = parts[0];
          if (!isNaN(itemIdx)) {
            const item = data[parseInt(itemIdx)];
            if (isPlaceholder(item?.id)) {
              skip = true;
            }
          }
        }
        if (!skip) {
          filtered.push(err);
        }
      }

      if (filtered.length > 0) {
        console.log(`  ✗ ${entry.file} — ${filtered.length} schema error(s)`);
        for (const err of filtered) {
          console.log(`    Path: ${err.instancePath || '(root)'} | ${err.message}`);
        }
        totalErrors += filtered.length;
      } else {
        console.log(`  ✓ ${entry.file} — all errors from placeholder items`);
      }
    }

    filesChecked++;
  }

  return { filesChecked, errors: totalErrors };
}

// ── Main ─────────────────────────────────────────────────────────

function main() {
  console.log('============================================================');
  console.log('  Learning API — Data File Validation');
  console.log('============================================================');

  const vocabResult = validateVocabularyFiles();
  const sentResult = validateSentenceFiles();
  const knowledgeResult = validateKnowledgeExerciseFiles();

  console.log('\n============================================================');
  console.log('  SUMMARY');
  console.log('============================================================');
  console.log(`  Vocabulary files:        ${vocabResult.filesChecked} checked, ${vocabResult.errors} error(s)`);
  console.log(`  Sentence files:          ${sentResult.filesChecked} checked, ${sentResult.errors} error(s)`);
  console.log(`  Knowledge exercise files: ${knowledgeResult.filesChecked} checked, ${knowledgeResult.errors} error(s)`);

  const totalErrors = vocabResult.errors + sentResult.errors + knowledgeResult.errors;
  const totalFiles = vocabResult.filesChecked + sentResult.filesChecked + knowledgeResult.filesChecked;
  console.log(`  ────────────────────────────────────────`);
  console.log(`  Total:                   ${totalFiles} checked, ${totalErrors} error(s)`);
  console.log('============================================================');

  if (totalErrors > 0) {
    process.exit(1);
  }
}

main();
