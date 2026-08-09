using 'main.bicep'

// Model selection is NOT hardcoded here — scripts/01-deploy-infra.sh overrides
// openAiModelName/Version/Sku from .env.deploy, which scripts/00-preflight.sh
// writes after probing what this subscription can actually deploy in this region.
// The values below are only a sensible default for a fresh checkout.

param appName = 'entraguard'
param environmentName = 'demo'
param location = 'eastus'

param openAiModelName = 'gpt-5-mini'
param openAiModelVersion = '2025-08-07'
param openAiSkuName = 'GlobalStandard'
param openAiCapacity = 30

// Set to your own Entra object ID to get direct Log Analytics read access.
param operatorObjectId = ''
