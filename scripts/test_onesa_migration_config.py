"""Source and Compose-config boundaries; no image, network or database is started."""

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from urllib.parse import urlsplit

PRODUCT = "factory"
PREFIX = "FACTORY"
ROOT = Path(__file__).resolve().parents[1]
PREPARED = ROOT / "deploy/onesa"
REQUIRE_COMPOSE = "--require-compose" in sys.argv
if REQUIRE_COMPOSE:
    sys.argv.remove("--require-compose")


def document(name):
    return json.loads((PREPARED / name).read_text())


def environment(name):
    values = {}
    for line in (PREPARED / name).read_text().splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        key, separator, value = line.partition("=")
        if not separator or key in values:
            raise ValueError("Invalid or duplicate example environment key")
        values[key] = value
    return values


class ConfigurationBoundaries(unittest.TestCase):
    def setUp(self):
        self.db = document("compose.database.yaml")
        self.service = self.db["services"]["postgres"]
        self.overlay = document("compose.application-database.yaml")

    def test_database_is_separate_opt_in_and_has_no_public_listener(self):
        self.assertEqual(set(self.db["services"]), {"postgres"})
        self.assertEqual(self.service["profiles"], ["candidate-database"])
        self.assertNotIn("ports", self.service)
        self.assertNotIn("network_mode", self.service)
        self.assertNotIn("privileged", self.service)
        self.assertEqual(set(self.service["networks"]), {"database"})
        self.assertTrue(self.db["networks"]["database"]["internal"])
        self.assertEqual(self.service["cap_drop"], ["ALL"])
        self.assertIn("no-new-privileges:true", self.service["security_opt"])

    def test_storage_identity_and_resource_choices_are_explicit(self):
        volume = self.db["volumes"]["postgres_data"]
        self.assertTrue(volume["external"])
        self.assertIn(PREFIX + "_POSTGRES_VOLUME:?", volume["name"])
        mount = next(v for v in self.service["volumes"] if v["type"] == "volume")
        self.assertTrue(self.service["environment"]["PGDATA"].startswith(mount["target"] + "/"))
        for key in ("image", "platform", "user", "mem_limit", "cpus", "shm_size"):
            self.assertIn(":?", self.service[key])
        self.assertTrue(self.service["read_only"])
        self.assertNotIn("POSTGRES_PASSWORD", self.service["environment"])
        self.assertEqual(self.service["environment"]["POSTGRES_USER"], "postgres")
        self.assertEqual(self.service["environment"]["POSTGRES_PASSWORD_FILE"],
                         "/run/secrets/postgres_bootstrap_password")
        self.assertIn(":?", self.db["secrets"]["postgres_bootstrap_password"]["file"])
        self.assertIn("@sha256:", environment("database.env.example")[PREFIX + "_POSTGRES_IMAGE"])

    def test_tls_files_and_hba_fail_closed_for_plaintext_and_named_admin(self):
        for setting in ("ssl=on", "ssl_min_protocol_version=TLSv1.2",
                        "password_encryption=scram-sha-256",
                        "hba_file=/etc/postgresql/pg_hba.conf"):
            self.assertIn(setting, self.service["command"])
        for mount in self.service["volumes"]:
            if mount["type"] == "bind":
                self.assertTrue(mount["read_only"])
                self.assertFalse(mount["bind"]["create_host_path"])
        rules = [line.split() for line in (PREPARED / "postgres.pg_hba.conf").read_text().splitlines()
                 if line.strip() and not line.lstrip().startswith("#")]
        self.assertIn(["local", "all", "postgres", "peer"], rules)
        self.assertTrue(all(rule[-1] != "trust" for rule in rules))
        for subnet in ("0.0.0.0/0", "::/0"):
            self.assertIn(["hostnossl", "all", "all", subnet, "reject"], rules)
            deny = ["hostssl", "all", "postgres", subnet, "reject"]
            allow = ["hostssl", "all", "all", subnet, "scram-sha-256"]
            self.assertLess(rules.index(deny), rules.index(allow))

    def test_canonical_reset_origins_provider_keys_and_existing_identities(self):
        if PRODUCT == "factory":
            api, worker = environment("api.env.example"), environment("worker.env.example")
            self.assertEqual(api["ALLOWED_ORIGIN"], "https://factory.onesa.eu")
            for role in (api, worker):
                self.assertEqual(role["PASSWORD_RESET_ORIGIN"], "https://factory.onesa.eu")
                self.assertEqual(role["BACKEND_DEPLOYMENT_MODE"], "render-bundle")
                self.assertEqual(role["EMAIL_PROVIDER"], "resend")
                self.assertIn("preserve-existing", role["DASHBOARD_SERVICE_TOKEN"])
            self.assertEqual(api["RENDER_SERVICE_ROLE"], "web")
            self.assertEqual(worker["RENDER_SERVICE_ROLE"], "worker")
            self.assertNotIn("RESEND_API_KEY", api)
            self.assertEqual(api["EMAIL_DELIVERY_CONFIGURED"], "false")
            self.assertIn("notifications@factory.onesa.eu", worker["RESEND_FROM"])
            self.assertIn("preserve-approved", worker["RESEND_ALLOWED_RECIPIENT_DOMAINS"])
            self.assertIn("preserve-existing-singleton", worker["MqttClientId"])
        else:
            runtime = environment("runtime.env.example")
            origin = "https://logistics.onesa.eu"
            self.assertEqual(runtime["ALLOWED_ORIGIN"], origin)
            self.assertEqual(runtime["CORS_ALLOWED_ORIGINS"], origin)
            self.assertEqual(runtime["PASSWORD_RESET_BASE_URL"], origin + "/#/reset-password")
            self.assertEqual(urlsplit(runtime["PASSWORD_RESET_BASE_URL"]).fragment, "/reset-password")
            self.assertEqual(runtime["EMAIL_PROVIDER"], "resend")
            self.assertIn("logistics@mail.onesa.eu", runtime["EMAIL_FROM"])
            self.assertIn("RESEND_API_KEY", runtime)
            self.assertNotIn("RESEND_FROM", runtime)
            self.assertEqual(runtime["EMAIL_NOTIFICATIONS_ENABLED"], "false")
            self.assertEqual(runtime["RUN_NODE_MIGRATIONS"], "false")
            self.assertEqual(runtime["Database__AutoMigrate"], "false")
            self.assertNotIn("MIGRATION_DATABASE_URL", runtime)
            self.assertIn("preserve-existing", runtime["RESET_DELIVERY_KEY"])

    def test_external_primary_and_verified_client_tls_remain_supported(self):
        if PRODUCT == "factory":
            for filename, dotnet in (("api.env.example", "DEVICE_DATABASE_CONNECTION"),
                                     ("worker.env.example", "TELEMETRY_DATABASE_CONNECTION")):
                values = environment(filename)
                self.assertIn("<current-or-qualified-database-host>", values["DATABASE_URL"])
                self.assertEqual(values["DATABASE_SSL_MODE"], "verify-full")
                self.assertIn("SSL Mode=VerifyFull", values[dotnet])
                self.assertIn("Root Certificate=/run/secrets/aiven-ca.pem", values[dotnet])
                self.assertIn("DATABASE_CA_CERT", values)
        else:
            values = environment("runtime.env.example")
            self.assertIn("<current-or-qualified-database-host>", values["DATABASE_URL"])
            self.assertIn("sslmode=verify-full", values["DATABASE_URL"])
            self.assertIn("sslrootcert=/run/onesa-db-ca/root.crt", values["DATABASE_URL"])
            self.assertIn("SSL Mode=VerifyFull", values["ConnectionStrings__DefaultConnection"])
            self.assertIn("Root Certificate=/run/onesa-db-ca/root.crt",
                          values["ConnectionStrings__DefaultConnection"])

    def test_overlay_changes_only_network_and_required_public_ca(self):
        self.assertTrue(self.overlay["networks"]["database"]["external"])
        self.assertEqual(self.overlay["networks"]["database"]["name"],
                         self.db["networks"]["database"]["name"])
        if PRODUCT == "factory":
            self.assertEqual(set(self.overlay["services"]), {"api", "worker"})
            for service in self.overlay["services"].values():
                self.assertEqual(set(service), {"networks"})
                self.assertEqual(set(service["networks"]), {"default", "database"})
        else:
            self.assertEqual(set(self.overlay["services"]), {"app"})
            app = self.overlay["services"]["app"]
            self.assertEqual(set(app), {"networks", "volumes"})
            mount, = app["volumes"]
            self.assertEqual(mount["target"], "/run/onesa-db-ca/root.crt")
            self.assertTrue(mount["read_only"])
            self.assertFalse(mount["bind"]["create_host_path"])


class ComposeConfigurationOnly(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which("docker"):
            if REQUIRE_COMPOSE:
                raise AssertionError("Docker Compose is required for this CI configuration check")
            raise unittest.SkipTest("Docker Compose not available; use --require-compose in CI")

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        temporary = Path(self.temporary.name)
        tls = temporary / "tls"
        tls.mkdir()
        for name in ("server.crt", "server.key", "root.crt"):
            (tls / name).write_text("synthetic-config-fixture-not-a-certificate\n")
        password = temporary / "bootstrap-password"
        password.write_text("synthetic-config-fixture-not-a-production-secret\n")
        password.chmod(0o600)
        self.env = {key: os.environ[key] for key in ("PATH", "HOME") if key in os.environ}
        self.env["COMPOSE_DISABLE_ENV_FILE"] = "1"
        self.env.update({
            PREFIX + "_POSTGRES_IMAGE": "postgres@sha256:" + "0" * 64,
            PREFIX + "_POSTGRES_PLATFORM": "linux/amd64",
            PREFIX + "_POSTGRES_UID": "10001",
            PREFIX + "_POSTGRES_GID": "10001",
            PREFIX + "_POSTGRES_MEMORY_LIMIT": "512m",
            PREFIX + "_POSTGRES_CPU_LIMIT": "1",
            PREFIX + "_POSTGRES_SHM_SIZE": "64m",
            PREFIX + "_POSTGRES_MAX_CONNECTIONS": "40",
            PREFIX + "_POSTGRES_VOLUME": "onesa-fixture-retained-volume",
            PREFIX + "_POSTGRES_PASSWORD_FILE": str(password),
            PREFIX + "_POSTGRES_TLS_DIR": str(tls),
            "LOGISTICS_DATABASE_CA_FILE": str(tls / "root.crt"),
        })

    def compose(self, files, *, env=None):
        command = ["docker", "compose", "--env-file", os.devnull]
        for file in files:
            command += ["-f", str(file)]
        command += ["--profile", "candidate-database", "config", "--format", "json"]
        return subprocess.run(command, env=self.env if env is None else env,
                              capture_output=True, text=True, timeout=30, check=False)

    def test_candidate_renders_without_starting_images_or_services(self):
        result = self.compose([PREPARED / "compose.database.yaml"])
        self.assertEqual(result.returncode, 0, result.stderr)
        rendered = json.loads(result.stdout)
        postgres = rendered["services"]["postgres"]
        self.assertFalse(postgres.get("ports"))
        self.assertEqual(postgres["user"], "10001:10001")
        self.assertEqual(postgres["environment"]["PGDATA"], "/var/lib/postgresql/data/pgdata")
        self.assertTrue(rendered["volumes"]["postgres_data"]["external"])
        self.assertEqual(rendered["volumes"]["postgres_data"]["name"], "onesa-fixture-retained-volume")
        self.assertTrue(rendered["networks"]["database"]["internal"])

    def test_missing_reviewed_image_cannot_silently_use_a_default(self):
        env = dict(self.env)
        del env[PREFIX + "_POSTGRES_IMAGE"]
        result = self.compose([PREPARED / "compose.database.yaml"], env=env)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(PREFIX + "_POSTGRES_IMAGE", result.stderr)

    def test_app_overlay_keeps_existing_primary_ingress_and_worker_default(self):
        # Structural fixture for the retained app contract, not a replacement deployment.
        image = "example.invalid/application:configuration-fixture"
        retained = {"DATABASE_URL": "postgresql://existing-primary.invalid/retained"}
        if PRODUCT == "factory":
            base = {"name": "factory-config-fixture", "services": {
                "api": {"image": image, "environment": retained,
                        "networks": {"default": {}, "basyx": {}}},
                "worker": {"image": image, "environment": retained}},
                "networks": {"basyx": {"name": "fixture-retained-ingress", "external": True}}}
            expected = {"api": {"default", "basyx", "database"},
                        "worker": {"default", "database"}}
        else:
            base = {"name": "logistics-config-fixture", "services": {
                "app": {"image": image, "environment": retained, "networks": {"ingress": {}}}},
                "networks": {"ingress": {"name": "fixture-retained-ingress", "external": True}}}
            expected = {"app": {"ingress", "database"}}
        base_file = Path(self.temporary.name) / "retained-application.json"
        base_file.write_text(json.dumps(base))
        result = self.compose([base_file, PREPARED / "compose.application-database.yaml"])
        self.assertEqual(result.returncode, 0, result.stderr)
        rendered = json.loads(result.stdout)
        for name, networks in expected.items():
            service = rendered["services"][name]
            self.assertEqual(set(service["networks"]), networks)
            self.assertEqual(service["environment"], retained)
            self.assertEqual(service["image"], image)


if __name__ == "__main__":
    unittest.main()
