{{/*
Expand the name of the chart.
*/}}
{{- define "squid-agent.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "squid-agent.fullname" -}}
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
{{- define "squid-agent.labels" -}}
helm.sh/chart: {{ include "squid-agent.name" . }}-{{ .Chart.Version | replace "+" "_" }}
{{ include "squid-agent.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "squid-agent.selectorLabels" -}}
app.kubernetes.io/name: {{ include "squid-agent.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Agent service account name
*/}}
{{- define "squid-agent.agent-sa-name" -}}
{{ include "squid-agent.fullname" . }}-agent
{{- end }}

{{/*
Script pod service account name
*/}}
{{- define "squid-agent.script-sa-name" -}}
{{- if .Values.scriptPod.serviceAccount }}
{{- .Values.scriptPod.serviceAccount }}
{{- else }}
{{ include "squid-agent.fullname" . }}-script
{{- end }}
{{- end }}

{{/*
PVC name
*/}}
{{- define "squid-agent.pvc-name" -}}
{{ include "squid-agent.fullname" . }}-workspace
{{- end }}
