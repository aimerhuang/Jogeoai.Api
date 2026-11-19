import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";
import { mkdir, open, readdir, readFile, rename, unlink } from "node:fs/promises";
import { join } from "node:path";

import { resultSubmissionSchema, type ResultSubmission } from "@aivc/contracts";

interface EncryptedEnvelope {
  version: 1;
  iv: string;
  authTag: string;
  ciphertext: string;
}

function encrypt(value: ResultSubmission, key: Buffer): EncryptedEnvelope {
  const iv = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", key, iv);
  const ciphertext = Buffer.concat([cipher.update(JSON.stringify(value), "utf8"), cipher.final()]);
  return {
    version: 1,
    iv: iv.toString("base64"),
    authTag: cipher.getAuthTag().toString("base64"),
    ciphertext: ciphertext.toString("base64"),
  };
}

function decrypt(envelope: EncryptedEnvelope, key: Buffer): ResultSubmission {
  const decipher = createDecipheriv("aes-256-gcm", key, Buffer.from(envelope.iv, "base64"));
  decipher.setAuthTag(Buffer.from(envelope.authTag, "base64"));
  const plaintext = Buffer.concat([
    decipher.update(Buffer.from(envelope.ciphertext, "base64")),
    decipher.final(),
  ]).toString("utf8");
  return resultSubmissionSchema.parse(JSON.parse(plaintext));
}

export async function spoolSubmission(
  directory: string,
  key: Buffer,
  submission: ResultSubmission,
): Promise<string> {
  await mkdir(directory, { recursive: true, mode: 0o700 });
  const baseName = `${submission.taskId}-${submission.leaseVersion}-${randomBytes(12).toString("hex")}.json.enc`;
  const target = join(directory, baseName);
  const temporary = `${target}.tmp`;
  const file = await open(temporary, "wx", 0o600);
  try {
    await file.writeFile(JSON.stringify(encrypt(submission, key)));
    await file.sync();
  } finally {
    await file.close();
  }
  await rename(temporary, target);
  return target;
}

export async function flushSpool(
  directory: string,
  key: Buffer,
  submit: (submission: ResultSubmission) => Promise<void>,
): Promise<number> {
  await mkdir(directory, { recursive: true, mode: 0o700 });
  const files = (await readdir(directory)).filter((file) => file.endsWith(".json.enc")).sort();
  let flushed = 0;
  for (const file of files) {
    const path = join(directory, file);
    const envelope = JSON.parse(await readFile(path, "utf8")) as EncryptedEnvelope;
    const submission = decrypt(envelope, key);
    await submit(submission);
    await unlink(path);
    flushed += 1;
  }
  return flushed;
}
