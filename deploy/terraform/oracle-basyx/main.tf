data "oci_identity_availability_domains" "available" {
  compartment_id = var.tenancy_ocid
}

data "oci_core_images" "ubuntu_arm64" {
  compartment_id           = var.compartment_ocid
  operating_system         = "Canonical Ubuntu"
  operating_system_version = "24.04"
  shape                    = "VM.Standard.A1.Flex"
  sort_by                  = "TIMECREATED"
  sort_order               = "DESC"
}

locals {
  availability_domain = data.oci_identity_availability_domains.available.availability_domains[var.availability_domain_number].name
  ubuntu_image_id     = data.oci_core_images.ubuntu_arm64.images[0].id
}

resource "oci_core_vcn" "basyx" {
  compartment_id = var.compartment_ocid
  cidr_blocks    = ["10.42.0.0/16"]
  display_name   = "${var.resource_name_prefix}-vcn"
  dns_label      = "basyxvcn"
}

resource "oci_core_internet_gateway" "basyx" {
  compartment_id = var.compartment_ocid
  vcn_id         = oci_core_vcn.basyx.id
  display_name   = "${var.resource_name_prefix}-internet-gateway"
  enabled        = true
}

resource "oci_core_route_table" "public" {
  compartment_id = var.compartment_ocid
  vcn_id         = oci_core_vcn.basyx.id
  display_name   = "${var.resource_name_prefix}-public-routes"

  route_rules {
    destination       = "0.0.0.0/0"
    destination_type  = "CIDR_BLOCK"
    network_entity_id = oci_core_internet_gateway.basyx.id
  }
}

resource "oci_core_security_list" "basyx" {
  compartment_id = var.compartment_ocid
  vcn_id         = oci_core_vcn.basyx.id
  display_name   = "${var.resource_name_prefix}-security-list"

  ingress_security_rules {
    protocol = "6"
    source   = var.ssh_ingress_cidr

    tcp_options {
      min = 22
      max = 22
    }
  }

  ingress_security_rules {
    protocol = "6"
    source   = "0.0.0.0/0"

    tcp_options {
      min = 443
      max = 443
    }
  }

  egress_security_rules {
    protocol    = "all"
    destination = "0.0.0.0/0"
  }
}

resource "oci_core_subnet" "public" {
  compartment_id             = var.compartment_ocid
  vcn_id                     = oci_core_vcn.basyx.id
  cidr_block                 = "10.42.1.0/24"
  display_name               = "${var.resource_name_prefix}-public-subnet"
  dns_label                  = "basyx"
  prohibit_public_ip_on_vnic = false
  route_table_id             = oci_core_route_table.public.id
  security_list_ids          = [oci_core_security_list.basyx.id]
}

resource "oci_core_instance" "basyx" {
  availability_domain = local.availability_domain
  compartment_id      = var.compartment_ocid
  display_name        = var.instance_display_name
  shape               = "VM.Standard.A1.Flex"

  shape_config {
    ocpus         = var.ocpus
    memory_in_gbs = var.memory_in_gbs
  }

  create_vnic_details {
    assign_public_ip = true
    display_name     = "${var.resource_name_prefix}-vnic"
    hostname_label   = var.instance_name
    subnet_id        = oci_core_subnet.public.id
  }

  source_details {
    source_id               = local.ubuntu_image_id
    source_type             = "image"
    boot_volume_size_in_gbs = var.boot_volume_size_in_gbs
  }

  metadata = {
    ssh_authorized_keys = file(pathexpand(var.ssh_public_key_path))
    user_data = base64encode(templatefile("${path.module}/cloud-init.yaml.tftpl", {
      instance_name = var.instance_name
    }))
  }

  lifecycle {
    precondition {
      condition     = var.ocpus * 6 >= var.memory_in_gbs
      error_message = "Ampere A1 allows at most 6 GB of memory per OCPU."
    }
  }
}
