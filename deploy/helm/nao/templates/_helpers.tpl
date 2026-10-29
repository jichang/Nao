{{- define "nao.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "nao.fullname" -}}
{{- printf "%s-%s" .Release.Name (include "nao.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "nao.labels" -}}
app.kubernetes.io/name: {{ include "nao.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "nao.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "nao.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- required "serviceAccount.name is required when serviceAccount.create is false" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "nao.image" -}}
{{- $name := index . 0 -}}
{{- $image := index . 1 -}}
{{- printf "%s@%s" (required (printf "%s repository is required" $name) $image.repository) (required (printf "%s digest is required" $name) $image.digest) -}}
{{- end -}}
