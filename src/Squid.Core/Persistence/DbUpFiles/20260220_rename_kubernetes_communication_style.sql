UPDATE machine
SET endpoint = REPLACE(endpoint, '"CommunicationStyle":"Kubernetes"', '"CommunicationStyle":"KubernetesApi"')
WHERE endpoint LIKE '%"CommunicationStyle":"Kubernetes"%'
  AND endpoint NOT LIKE '%"CommunicationStyle":"KubernetesApi"%'
  AND endpoint NOT LIKE '%"CommunicationStyle":"KubernetesAgent"%';
