#!/bin/bash
set -e

SHARED_DIRECTORY=${SHARED_DIRECTORY:-/squid-workspace}

mkdir -p "$SHARED_DIRECTORY"
chmod 0777 "$SHARED_DIRECTORY"

echo "$SHARED_DIRECTORY *(rw,sync,no_subtree_check,no_root_squash,fsid=0)" > /etc/exports

rpcbind
rpc.nfsd --no-nfs-version 2 --no-nfs-version 3
exportfs -arv
rpc.mountd --no-nfs-version 2 --no-nfs-version 3 --foreground
