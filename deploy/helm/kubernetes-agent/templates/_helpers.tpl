{{/*
Expand the name of the chart.
*/}}
{{- define "kubernetes-agent.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "kubernetes-agent.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "kubernetes-agent.labels" -}}
helm.sh/chart: {{ include "kubernetes-agent.name" . }}-{{ .Chart.Version | replace "+" "_" }}
{{ include "kubernetes-agent.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "kubernetes-agent.selectorLabels" -}}
app.kubernetes.io/name: {{ include "kubernetes-agent.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Tentacle service account name
*/}}
{{- define "kubernetes-agent.tentacle-sa-name" -}}
{{- printf "%s-tentacle" (include "kubernetes-agent.fullname" .) -}}
{{- end }}

{{/*
Script pod service account name
*/}}
{{- define "kubernetes-agent.script-sa-name" -}}
{{- if .Values.scriptPod.serviceAccount }}
{{- .Values.scriptPod.serviceAccount }}
{{- else }}
{{- printf "%s-script" (include "kubernetes-agent.fullname" .) -}}
{{- end }}
{{- end }}

{{/*
PVC name
*/}}
{{- define "kubernetes-agent.pvc-name" -}}
{{- printf "%s-workspace" (include "kubernetes-agent.fullname" .) -}}
{{- end }}

{{/*
Storage mode detection — Custom PVC (storageClassName or volumeName set)
*/}}
{{- define "kubernetes-agent.useCustomPvc" -}}
{{- if or .Values.workspace.storageClassName .Values.workspace.volumeName -}}true{{- end -}}
{{- end -}}

{{/*
Storage mode detection — Built-in NFS (default: no custom PVC, no external NFS server)
*/}}
{{- define "kubernetes-agent.useBuiltinNfs" -}}
{{- if and (not (include "kubernetes-agent.useCustomPvc" .)) (not .Values.workspace.nfs.server) -}}true{{- end -}}
{{- end -}}

{{/*
Storage mode detection — External NFS (user-provided NFS server)
*/}}
{{- define "kubernetes-agent.useExternalNfs" -}}
{{- if and (not (include "kubernetes-agent.useCustomPvc" .)) .Values.workspace.nfs.server -}}true{{- end -}}
{{- end -}}
