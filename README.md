# Develop DAPR Function to deploy in ACA

## Create and deploy Azure Function

This app was created using instructions from: [Create your first containerized functions on Azure Container Apps](https://learn.microsoft.com/en-us/azure/azure-functions/functions-deploy-container-apps)

```bash
cd fn-dapr-trigger-aca
```

The .NET function project was created using this command:

```bash
func init --worker-runtime dotnet-isolated --docker
```

Create a new HTTP function:

```bash
func new --name HealthCheck --template "HTTP trigger"
```

Additionally we changed the `AuthorizationLevel` to `Anonymous` in `HelthCheck.cs` to simplify testing.

### Add DAPR topic trigger function

Instructions based on: [Dapr Topic trigger for Azure Functions](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-dapr-trigger-topic)

Added DAPR extensions and CloudEvents (already added):

```bash
dotnet add package CloudNative.CloudEvents
dotnet add package Dapr.Client
dotnet add package Dapr.AzureFunctions.Extension
dotnet add package Microsoft.Azure.Functions.Worker.Extensions.Dapr
dotnet add package Microsoft.Azure.Functions.Extensions.Dapr.Core
dotnet add package Microsoft.Azure.WebJobs.Extensions.Dapr
```

Created components directory with `messagebus.yaml` configured to listen in local redis instance:

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: messagebus
spec:
  type: pubsub.redis
  metadata:
  - name: redisHost
    value: localhost:6379
  - name: redisPassword
    value: ""
```

Updated `local.settings.json` with one additional `PubSubName` parameter:

```json
{
    "IsEncrypted": false,
    "Values": {
        "AzureWebJobsStorage": "UseDevelopmentStorage=true",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
        "PubSubName": "messagebus" 
    }
}
```

Added new `DaprConsumeTopicMessage.cs` class.

Test locally using:

```bash
dapr run -f .
```

You should see 2 functions:

```txt
== APP - functionapp == Functions:
== APP - functionapp ==
== APP - functionapp ==         HealthCheck: [GET,POST] http://localhost:7071/api/HealthCheck
== APP - functionapp ==
== APP - functionapp ==         ConsumeTopicMessage: daprTopicTrigger
```

```bash
dapr publish --pubsub messagebus --publish-app-id functionapp --topic a --data 'This is a test'
```

Expect to see the following output on the terminal running the functionapp:

```txt
== APP - functionapp == [2025-03-18T19:57:19.826Z] C# function processed a ConsumeTopicMessage request from the Dapr Runtime.
== APP - functionapp == [2025-03-18T19:57:19.827Z] Topic received a message: "This is a test".
```

### Create Azure Container Registry (ACR)

Create resource group:

```bash
REGION=eastus2
RG=AzureFunctionsContainers-rg
az group create --name AzureFunctionsContainers-rg --location $REGION
```

Create ACR:

```bash
ID=<UniqueId>
ACR_NAME=acrfunctionaca$ID
az acr create --resource-group $RG --name $ACR_NAME --sku Basic
```

Get `loginServer` value:

```bash
ACR_SERVER=$(az acr show --name $ACR_NAME --resource-group $RG --query "loginServer" --output tsv)
```

Login to ACR:

```bash
az acr login --name $ACR_NAME
```

### Build and deploy docker image to ACR

Use this command to build a docker image:

```bash
IMAGE_NAME=fn-dapr
docker build --tag $ACR_SERVER/$IMAGE_NAME:v1.0.0 .
```

Push image to ACR:

```bash
docker push $ACR_SERVER/$IMAGE_NAME:v1.0.0
```

### Create Container App Environment and Managed Identity

Create storage account:

```bash
STORAGE=acrfunctionacastg$ID
az storage account create --name $STORAGE --location $REGION --resource-group $RG --sku Standard_LRS \
--allow-blob-public-access false --allow-shared-key-access false
```

Create Container App Environment:

```bash
ENVIRONMENT=fn-dapr-cae
az containerapp env create --name $ENVIRONMENT --enable-workload-profiles --resource-group $RG --location $REGION
```

Create a managed identity and use the returned principalId to grant it both access to your storage account and pull permissions in your registry instance:

```bash
USER_IDENTITY_NAME=aca-user-$ID
az identity create --name $USER_IDENTITY_NAME --resource-group $RG --location $REGION
PRINCIPAL_ID=$(az identity show --name $USER_IDENTITY_NAME --resource-group $RG --query principalId --output tsv)
ACR_ID=$(az acr show --name $ACR_NAME --query id --output tsv | tr -d '\r')
STORAGE_ID=$(az storage account show --resource-group $RG --name $STORAGE --query 'id' -o tsv | tr -d '\r')
```

Note that if role assignment command fails in git bash, use WSL instead:

```bash
az role assignment create --assignee-object-id $PRINCIPAL_ID \
--role AcrPull --scope $ACR_ID --assignee-principal-type ServicePrincipal
az role assignment create --assignee-object-id $PRINCIPAL_ID \
--role "Storage Blob Data Owner" --scope $STORAGE_ID --assignee-principal-type ServicePrincipal
```

### Create Azure Service Bus

Create Azure Service Bus namespace:

```bash
SERVICE_BUS_NAMESPACE=servicebus$ID
az servicebus namespace create --name $SERVICE_BUS_NAMESPACE --resource-group $RG --location $REGION
```

Create a topic named "A"

```bash
TOPIC_NAME=a
az servicebus topic create --name $TOPIC_NAME --namespace-name $SERVICE_BUS_NAMESPACE --resource-group $RG
```

Create a Subscription on Topic A

```bash
az servicebus topic subscription create --name $TOPIC_NAME --topic-name $TOPIC_NAME \
--namespace-name $SERVICE_BUS_NAMESPACE --resource-group $RG
```

Navigate to the Azure Portal, go to the Service Bus namespace, and use the Service Bus Explorer to verify the subscription and test message delivery.

Grant authorization to send and receive data to the user managed identity:

```bash
SERVICE_BUS_ID=$(az servicebus namespace show --name $SERVICE_BUS_NAMESPACE -g $RG --query id --output tsv | tr -d '\r')

az role assignment create --assignee-object-id $PRINCIPAL_ID \
--role "Azure Service Bus Data Sender" --scope $SERVICE_BUS_ID --assignee-principal-type ServicePrincipal

az role assignment create --assignee-object-id $PRINCIPAL_ID \
--role "Azure Service Bus Data Receiver" --scope $SERVICE_BUS_ID --assignee-principal-type ServicePrincipal
```

### Create DAPR components configuration

Create Dapr Components (see [Dapr components in ACA schema](https://learn.microsoft.com/en-us/azure/container-apps/dapr-components)):

```bash
cat <<EOF > tmp.yaml
componentType: pubsub.azure.servicebus.topics
version: v1
metadata:
  - name: azureClientId
    value: $CLIENT_ID
  - name: namespaceName
    value: $SERVICE_BUS_NAMESPACE.servicebus.windows.net
  - name: consumerID
    value: $TOPIC_NAME
EOF
az containerapp env dapr-component set --name $ENVIRONMENT --resource-group $RG --dapr-component-name messagebus --yaml tmp.yaml 
rm tmp.yaml
```

**NOTE:** it is recommended to store the clientId in Azure Key Vault and referencing it as secret reference. Azure Key Vault can be setup as a [secretstore dapr component](https://docs.dapr.io/developing-applications/integrations/azure/azure-authentication/howto-mi)

### Create and Deploy Azure Function

Create function app:

```bash
APP_NAME=fn-dapr-app
UAMI_RESOURCE_ID=$(az identity show --name $USER_IDENTITY_NAME --resource-group $RG --query id -o tsv)
export MSYS_NO_PATHCONV=1
az functionapp create --name $APP_NAME --resource-group $RG --storage-account $STORAGE \
--environment $ENVIRONMENT --functions-version 4 --assign-identity $UAMI_RESOURCE_ID
```

Configure function app to use our Docker image and enable DAPR:

```bash
az resource patch --resource-group $RG --name $APP_NAME --resource-type "Microsoft.Web/sites" --properties \
"{ \"siteConfig\": { \"linuxFxVersion\": \"DOCKER|$ACR_SERVER/$IMAGE_NAME:v1.0.0\", \
\"acrUseManagedIdentityCreds\": true, \"acrUserManagedIdentityID\":\"$UAMI_RESOURCE_ID\", \
\"appSettings\": [{\"name\": \"DOCKER_REGISTRY_SERVER_URL\", \"value\": \"$ACR_SERVER\"}, \
{\"name\": \"PubSubName\", \"value\": \"messagebus\"}, {\"name\": \"DAPR_HTTP_PORT\", \"value\": \"3500\"}]}, \
\"DaprConfig\": { \"enabled\": \"true\", \"appId\": \"$APP_NAME\", \"appPort\": \"3001\", \
\"enableApiLogging\": \"true\", \"logLevel\": \"debug\"}}"
```

Restart the Azure Function to apply the updates in the new Docker image:

```bash
az functionapp restart --name $APP_NAME --resource-group $RG
```

Get HealthCheck function URL:

```bash
FUNCTION_URL=$(az functionapp function show --resource-group $RG --name $APP_NAME --function-name HealthCheck --query invokeUrlTemplate -o tsv)
```

Test using `curl` command:

```bash
curl $FUNCTION_URL
Welcome to Azure Functions!
```

## Deploy simple pub/sub demo app to Azure Container Apps

Change directory to:

```bash
cd aca-dapr-pubsub
```

Test ACA DAPR project locally:

```bash
dapr run --app-id publisher  --scheduler-host-address "" --components-path ../components/ -- dotnet run --project .
```

Use this command to build ACA image:

```bash
docker build --tag $ACR_SERVER/$ACA_IMAGE_NAME:v1.0.0 .
```

Push ACA image to ACR:

```bash
docker push $ACR_SERVER/$ACA_IMAGE_NAME:v1.0.0
```

Deploy ACA:

```bash
az containerapp create \
    --name $ACA_IMAGE_NAME \
    --resource-group $RG \
    --environment $ENVIRONMENT \
    --user-assigned $UAMI_RESOURCE_ID \
    --image $ACR_SERVER/$ACA_IMAGE_NAME:v1.0.0 \
    --registry-identity $UAMI_RESOURCE_ID \
    --registry-server $ACR_SERVER \
    --cpu 0.5 --memory 1.0Gi \
    --ingress external \
    --target-port 8080 \
    --enable-dapr true \
    --dapr-app-id $ACA_IMAGE_NAME \
    --dapr-enable-api-logging
```

Get the endpoint URL for the deployed Azure Container App:

```bash
ACA_ENDPOINT=$(az containerapp show \
    --name $ACA_IMAGE_NAME \
    --resource-group $RG \
    --query properties.configuration.ingress.fqdn -o tsv)

echo "The endpoint URL for the deployed ACA is: https://$ACA_ENDPOINT"
```

Post a test message to the publish URL of the Azure Container App

```bash
curl -X POST "https://$ACA_ENDPOINT/publish/" \
    -H "Content-Type: application/json" \
    -d '{"message": "Hello, Dapr!"}'
```

Check the logs of Azure Function to confirm message is received:

```bash
Topic received a message: {"data":{"message":"{\"message\": \"Hello, Dapr!\"}","messageType":"OrderCreated"},"datacontenttype":"application/json","id":"fe58744a-ee47-4be2-a777-70f63d6f5a6a","pubsubname":"messagebus","source":"aca-dapr-pubsub","specversion":"1.0","time":"2025-03-21T18:32:03Z","topic":"a","traceid":"00-8c083cf5404eee65e454e174539b852b-24c4421906e68474-01","traceparent":"00-8c083cf5404eee65e454e174539b852b-24c4421906e68474-01","tracestate":"","type":"com.dapr.event.sent"}.
```

## Reference

* [Setup Azure Service Bus topics](https://docs.dapr.io/reference/components-reference/supported-pubsub/setup-azure-servicebus-topics/)
* [DAPR - Managed Identity Azure authentication](https://docs.dapr.io/developing-applications/integrations/azure/azure-authentication/howto-mi]
* [DAPR C# Service Bus sample](https://github.com/Azure-Samples/pubsub-dapr-csharp-servicebus)
* [Azure Functions deploy as container](https://learn.microsoft.com/en-us/azure/azure-functions/functions-how-to-custom-container)
* [Java DAPR Service Bus sample](https://azure.github.io/java-aks-aca-dapr-workshop/modules/03-assignment-3-azure-pub-sub/1-azure-service-bus.html)
