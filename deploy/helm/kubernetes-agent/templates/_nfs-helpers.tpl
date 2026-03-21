{{/*
Built-in NFS server name
*/}}
{{- define "kubernetes-agent.nfs.name" -}}
{{- printf "%s-nfs" (include "kubernetes-agent.fullname" .) | trunc 63 -}}
{{- end -}}

{{/*
Built-in NFS PersistentVolume name
*/}}
{{- define "kubernetes-agent.nfs.pv-name" -}}
{{- printf "%s-nfs-pv" (include "kubernetes-agent.fullname" .) | trunc 63 -}}
{{- end -}}

{{/*
Built-in NFS server DNS address (headless service)
*/}}
{{- define "kubernetes-agent.nfs.serverAddress" -}}
{{- printf "%s.%s.svc.cluster.local" (include "kubernetes-agent.nfs.name" .) .Release.Namespace -}}
{{- end -}}
