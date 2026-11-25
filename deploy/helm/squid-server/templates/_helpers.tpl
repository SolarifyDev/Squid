{{- define "squid-server.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "squid-server.fullname" -}}
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

{{- define "squid-server.labels" -}}
helm.sh/chart: {{ include "squid-server.name" . }}-{{ .Chart.Version | replace "+" "_" }}
{{ include "squid-server.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "squid-server.selectorLabels" -}}
app.kubernetes.io/name: {{ include "squid-server.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "squid-server.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "squid-server.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/* Secret the credentials come from: the operator's existing one, or the chart's own. */}}
{{- define "squid-server.secretName" -}}
{{- default (include "squid-server.fullname" .) .Values.existingSecret }}
{{- end }}

{{/* True when the chart renders its own Secret (some credential given inline, no existing Secret). */}}
{{- define "squid-server.ownsSecret" -}}
{{- if and (not .Values.existingSecret) (or .Values.admin.password .Values.database.connectionString .Values.redis.connectionString) }}true{{ end }}
{{- end }}

{{- define "squid-server.externalUrl" -}}
{{- if .Values.externalUrl }}
{{- .Values.externalUrl }}
{{- else if and .Values.ingress.enabled .Values.ingress.host }}
{{- printf "%s://%s" (ternary "https" "http" (not (empty .Values.ingress.tls.secretName))) .Values.ingress.host }}
{{- end }}
{{- end }}

{{/* Polling URL agents use; the port follows how polling is exposed. */}}
{{- define "squid-server.pollingUrl" -}}
{{- $p := .Values.polling }}
{{- if .Values.pollingUrl }}
{{- .Values.pollingUrl }}
{{- else if and $p.host (eq $p.mode "ingressPassthrough") }}
{{- printf "https://%s" $p.host }}
{{- else if and $p.host (eq $p.mode "loadBalancer") }}
{{- printf "https://%s:%v" $p.host $p.port }}
{{- else if and $p.host (eq $p.mode "nodePort") $p.nodePort.port }}
{{- printf "https://%s:%v" $p.host $p.nodePort.port }}
{{- end }}
{{- end }}

{{/* Rules values.schema.json cannot state clearly; types and allowed values live in the schema. */}}
{{- define "squid-server.validate" -}}
{{- if and (eq .Values.polling.mode "ingressPassthrough") (not .Values.polling.host) }}
{{- fail "polling.host is required when polling.mode is ingressPassthrough" }}
{{- end }}
{{- if and .Values.ingress.enabled (not .Values.ingress.host) }}
{{- fail "ingress.host is required when ingress.enabled is true" }}
{{- end }}
{{- end }}
