# Oracle Software Engineering Backend host

This Terraform module creates one Ubuntu 24.04 ARM64 VM on the OCI Ampere A1 Flex shape, plus an isolated VCN, public subnet, and internet gateway. It installs Docker and Docker Compose through cloud-init. The Software Engineering Backend hosts the Smart Factory API and singleton worker alongside the separate BaSyx stack. The existing module directory and Terraform resource addresses retain their historical names to preserve state identity.

The defaults request 2 OCPUs, 12 GB RAM, and a 50 GB boot volume. The variable validations cap a continuously running VM at Oracle's current monthly Always Free A1 allowance and cap its boot volume at the regional free-storage allowance. These checks cannot account for resources created outside this module, partial-month usage, future Oracle policy changes, provider classification, or A1 capacity; review Limits, Quotas and Usage plus the cost estimate before every apply.

SSH must be restricted to a specific CIDR; HTTPS on port 443 is also configured. BaSyx ports 8081–8086 are deliberately not exposed. Production access should use an authenticated HTTPS reverse proxy or Cloudflare Tunnel.

## 1. Authenticate with OCI

The default configuration uses a temporary browser-authenticated session, so a permanent API key is not required:

```bash
oci session authenticate --region eu-frankfurt-1 --profile-name DEFAULT
```

Complete sign-in and MFA in the browser. If the session expires, run the same command again. Permanent API-key authentication is also supported by setting `oci_auth = "ApiKey"` after configuring `~/.oci/config`.

## 2. Create local variables

```bash
cd deploy/terraform/oracle-basyx
cp terraform.tfvars.example terraform.tfvars
curl -4 https://ifconfig.me
```

Edit `terraform.tfvars` with the tenancy OCID, compartment OCID, and the returned IP followed by `/32`. `terraform.tfvars`, state, and plans are ignored by Git.

The root compartment uses the tenancy OCID as its compartment OCID. If you create a dedicated compartment, use that compartment's OCID instead.

### Display names and existing installations

Display labels are independent of DNS and the guest hostname:

| Variable | Default | Purpose |
| --- | --- | --- |
| `instance_display_name` | `Software Engineering Backend` | Instance display name in OCI. |
| `resource_name_prefix` | `software-engineering-backend` | Display-name prefix for the VCN, internet gateway, public route table, security list, public subnet, and primary VNIC. |
| `instance_name` | `basyx-server` | Existing VNIC DNS hostname and cloud-init guest hostname. Preserve its deployed value during a display-name update. |

The VCN DNS label remains `basyxvcn`, and the subnet DNS label remains `basyx`.
Changing display names does not require changing these labels, the hostname,
IP addresses, routes, security rules, or Terraform resource addresses. Do not
rename resources such as `oci_core_instance.basyx` to match their console labels.
Automatically created boot-volume and default-network-resource display names are
not explicitly managed by this module.

If display names were changed in the OCI Console, use the same values for these
display-only variables in the maintained configuration and the operator's
existing variable file. Keep `instance_name` at the actual deployed hostname.
This prevents a later apply from restoring the old display names. Do not replace
an existing variable file with the example or assume that this checkout contains
the production Terraform state.

For an existing installation, review a plan using its original state, variables,
and locked provider version. A naming-only change must show only the intended
`display_name` updates, or no changes if the names already match. Stop if it
proposes any creation, replacement, deletion, hostname/metadata change, or
network-rule change. In particular, this module selects the newest matching
Ubuntu image and manages complete route and security-rule configuration; an
unrelated image or network drift can appear in a fresh plan. Do not run a broad
apply to rename resources or suppress those differences with blanket
`ignore_changes`. The creation procedure below is for a new installation.

## 3. Review before creation

```bash
terraform init
terraform fmt -check
terraform validate
terraform plan -out=basyx.tfplan
```

Review the plan and the Oracle cost estimate. Do not apply if the Console shows a non-zero estimate or the shape is not marked **Always Free-eligible**.

## 4. Create and connect

```bash
terraform apply basyx.tfplan
terraform output -raw ssh_command
```

Cloud-init can take several minutes. After connecting:

```bash
cloud-init status --wait
docker compose version
```

The BaSyx Compose deployment and its Aiven password are intentionally separate from Terraform so the database credential is not written into Terraform state.
