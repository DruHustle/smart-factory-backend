variable "tenancy_ocid" {
  description = "OCI tenancy OCID."
  type        = string
}

variable "compartment_ocid" {
  description = "OCI compartment OCID in which to create the VM and network."
  type        = string
}

variable "region" {
  description = "OCI region."
  type        = string
  default     = "eu-frankfurt-1"
}

variable "oci_profile" {
  description = "Profile name in ~/.oci/config."
  type        = string
  default     = "DEFAULT"
}

variable "oci_auth" {
  description = "OCI provider authentication method. SecurityToken uses browser-authenticated CLI sessions."
  type        = string
  default     = "SecurityToken"
}

variable "instance_name" {
  description = "Legacy VNIC DNS and guest hostname. Preserve the existing value when changing display names."
  type        = string
  default     = "basyx-server"
}

variable "instance_display_name" {
  description = "OCI console display name for the shared backend instance; does not change its hostname."
  type        = string
  default     = "Software Engineering Backend"
}

variable "resource_name_prefix" {
  description = "OCI display-name prefix for the instance's network resources; does not change DNS labels or resource identities."
  type        = string
  default     = "software-engineering-backend"
}

variable "availability_domain_number" {
  description = "Zero-based availability-domain index. Change this if A1 capacity is unavailable."
  type        = number
  default     = 0
}

variable "ocpus" {
  description = "Ampere A1 OCPUs. Two continuously running OCPUs consume the current 1,500 Always Free OCPU-hours per month."
  type        = number
  default     = 2

  validation {
    condition     = var.ocpus > 0 && var.ocpus <= 2
    error_message = "ocpus must be between 1 and 2 so a continuously running VM stays within the 1,500 monthly Always Free OCPU-hours."
  }
}

variable "memory_in_gbs" {
  description = "Ampere A1 memory in GB. Twelve continuously allocated GB consume the current 9,000 Always Free GB-hours per month."
  type        = number
  default     = 12

  validation {
    condition     = var.memory_in_gbs >= 1 && var.memory_in_gbs <= 12
    error_message = "memory_in_gbs must be between 1 and 12 so a continuously running VM stays within the 9,000 monthly Always Free GB-hours."
  }
}

variable "boot_volume_size_in_gbs" {
  description = "Boot volume size in GB."
  type        = number
  default     = 50

  validation {
    condition     = var.boot_volume_size_in_gbs >= 47 && var.boot_volume_size_in_gbs <= 200
    error_message = "boot_volume_size_in_gbs must be between OCI's 47 GB minimum and the 200 GB regional Always Free block-storage allowance."
  }
}

variable "ssh_public_key_path" {
  description = "Path to the public SSH key installed for the ubuntu user."
  type        = string
  default     = "~/.ssh/id_ed25519.pub"
}

variable "ssh_ingress_cidr" {
  description = "IPv4 CIDR allowed to SSH, normally your current public IP with /32."
  type        = string

  validation {
    condition     = can(cidrhost(var.ssh_ingress_cidr, 0)) && var.ssh_ingress_cidr != "0.0.0.0/0"
    error_message = "Use a valid restricted CIDR; unrestricted SSH (0.0.0.0/0) is not allowed."
  }
}
