{{/*
Expand the name of the chart.
*/}}
{{- define "squid-tentacle.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "squid-tentacle.fullname" -}}
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
{{- define "squid-tentacle.labels" -}}
helm.sh/chart: {{ include "squid-tentacle.name" . }}-{{ .Chart.Version | replace "+" "_" }}
{{ include "squid-tentacle.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "squid-tentacle.selectorLabels" -}}
app.kubernetes.io/name: {{ include "squid-tentacle.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Tentacle service account name
*/}}
{{- define "squid-tentacle.tentacle-sa-name" -}}
{{ include "squid-tentacle.fullname" . }}-tentacle
{{- end }}

{{/*
Script pod service account name
*/}}
{{- define "squid-tentacle.script-sa-name" -}}
{{- if .Values.scriptPod.serviceAccount }}
{{- .Values.scriptPod.serviceAccount }}
{{- else }}
{{ include "squid-tentacle.fullname" . }}-script
{{- end }}
{{- end }}

{{/*
PVC name
*/}}
{{- define "squid-tentacle.pvc-name" -}}
{{ include "squid-tentacle.fullname" . }}-workspace
{{- end }}
