#!/usr/bin/env node
import { chmod, readFile, writeFile } from "node:fs/promises";

const sourcePath = process.argv[2] ?? ".env.prod";
const source = await readFile(sourcePath, "utf8");
const values = new Map();
for (const line of source.split(/\r?\n/)) {
  const match = line.match(/^([A-Za-z_][A-Za-z0-9_]*)=(.*)$/);
  if (match) {
    if (match[2].startsWith(`${match[1]}=`)) {
      throw new Error(`${match[1]} contains a duplicated variable assignment`);
    }
    values.set(match[1], match[2]);
  }
}
const defaults = new Map([
  ["EMAIL_PROVIDER", "ses"],
  ["SES_ENABLED", "true"],
  ["SES_REGION", "us-east-1"],
  ["SES_FROM", "Smart Factory <smartfactory.notifications@gmail.com>"],
  ["SES_ALLOW_ALL_RECIPIENTS", "false"],
  ["SES_ALLOWED_RECIPIENTS", "smartfactory.notifications@gmail.com"],
]);

const apiKeys = [
  "ASPNETCORE_ENVIRONMENT", "DATABASE_URL", "DATABASE_CA_CERT",
  "DEVICE_DATABASE_CONNECTION", "REDIS_URL", "JWT_SECRET",
  "AAS_PROVISIONING_TOKEN", "INGESTION_API_TOKEN", "DASHBOARD_SERVICE_TOKEN",
  "ALLOWED_ORIGIN", "MqttBrokerHost", "MqttBrokerPort", "MqttUsername",
  "MqttPassword", "MqttUseTls", "EDGE_SITE_ID", "EDGE_LINE_ID",
  "AAS_REPOSITORY_URL", "AAS_SUBMODEL_REPOSITORY_URL",
  "AAS_CONCEPT_DESCRIPTION_REPOSITORY_URL", "AAS_REGISTRY_URL",
  "AAS_SUBMODEL_REGISTRY_URL", "AASX_FILE_SERVER_URL", "AAS_OIDC_TOKEN_URL",
  "AAS_OIDC_DISCOVERY_URL", "AAS_OIDC_CLIENT_ID", "AAS_OIDC_CLIENT_SECRET",
  "AAS_OIDC_SCOPE", "AAS_REPOSITORY_REGISTRY_INTEGRATION",
  "AAS_ALLOW_UNAUTHENTICATED_LOCAL", "AAS_ALLOW_UNAUTHENTICATED_PRIVATE",
  "ENABLE_DEMO_ACCOUNTS", "ENABLE_DEMO_DATA",
];
const workerKeys = [
  "ASPNETCORE_ENVIRONMENT", "DASHBOARD_API_ORIGIN", "DATABASE_URL",
  "DATABASE_CA_CERT", "TELEMETRY_DATABASE_CONNECTION", "INGESTION_API_TOKEN",
  "DASHBOARD_SERVICE_TOKEN", "MqttBrokerHost", "MqttBrokerPort",
  "MqttUsername", "MqttPassword", "MqttUseTls", "MqttClientId", "MqttTopic",
  "MqttHeartbeatTopic", "EMAIL_PROVIDER", "SES_ENABLED", "SES_REGION",
  "SES_FROM", "SES_ALLOW_ALL_RECIPIENTS", "SES_ALLOWED_RECIPIENTS",
  "SES_ALLOWED_RECIPIENT_DOMAINS", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY",
];

function render(role, keys) {
  const lines = [
    "# Generated from .env.prod by scripts/split-render-env.mjs.",
    "# Keep this file out of Git and upload it only to the matching Render service.",
    `RENDER_SERVICE_ROLE=${role}`,
  ];
  for (const key of keys) lines.push(`${key}=${values.get(key) ?? defaults.get(key) ?? ""}`);
  return `${lines.join("\n")}\n`;
}

await writeFile(".env.prod.api", render("web", apiKeys), { mode: 0o600 });
await writeFile(".env.prod.worker", render("worker", workerKeys), { mode: 0o600 });
await chmod(".env.prod.api", 0o600);
await chmod(".env.prod.worker", 0o600);
console.log("Created .env.prod.api and .env.prod.worker with permissions 0600; values were not printed.");
