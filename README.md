# Develop DAPR Function to deploy in ACA

This app was created using instructions from: [Create your first containerized functions on Azure Container Apps](https://learn.microsoft.com/en-us/azure/azure-functions/functions-deploy-container-apps)

The .NET function project was created using this command:

```bash
func init --worker-runtime dotnet-isolated --docker
```

Create a new HTTP function:

```bash
func new --name HealthCheck --template "HTTP trigger"
```

Additionally we changed the `AuthorizationLevel` to `Anonymous` in `HelthCheck.cs` to simplify testing.

## Test function locally

Run this command:

```bash
func start
```

Test using this command:

```bash
curl http://localhost:7071/api/HealthCheck
Welcome to Azure Functions!
```

## Create Azure Container Registry (ACR)

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
az acr login --name ACR_NAME
```

## Build docker image

Use this command to build a docker image:

```bash
IMAGE_NAME=fn-dapr
docker build --tag $ACR_SERVER/$IMAGE_NAME:v1.0.0 .
```

Run container:

```bash
docker run --rm -p 8080:80 -it $ACR_SERVER/$IMAGE_NAME:v1.0.0
```

Test locally using `curl` command:

```bash
curl http://localhost:8080/api/HealthCheck
Welcome to Azure Functions!
```

Push image to ACR:

```bash
docker push $ACR_SERVER/$IMAGE_NAME:v1.0.0
```

## Deploy Azure Function

Create storage account:

```bash
STORAGE=acrfunctionacastg$ID
az storage account create --name $STORAGE --location $REGION --resource-group $RG --sku Standard_LRS \
--allow-blob-public-access false --allow-shared-key-access false
```

Create Container App plan

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

Create function app:

```bash
APP_NAME=fn-dapr-app
UAMI_RESOURCE_ID=$(az identity show --name $USER_IDENTITY_NAME --resource-group $RG --query id -o tsv)
export MSYS_NO_PATHCONV=1
az functionapp create --name $APP_NAME --storage-account $STORAGE --environment $ENVIRONMENT \
--workload-profile-name "Consumption" --resource-group $RG --functions-version 4 --assign-identity $UAMI_RESOURCE_ID
```

Configure function app:

```bash
az resource patch --resource-group $RG --name $APP_NAME --resource-type "Microsoft.Web/sites" --properties \
"{ \"siteConfig\": { \"linuxFxVersion\": \"DOCKER|$ACR_SERVER/$IMAGE_NAME:v1.0.0\", \
\"acrUseManagedIdentityCreds\": true, \"acrUserManagedIdentityID\":\"$UAMI_RESOURCE_ID\", \
\"appSettings\": [{\"name\": \"DOCKER_REGISTRY_SERVER_URL\", \"value\": \"$ACR_SERVER\"}]}}"
```

Update `webStorage` setting by deleting it first:

```bash
az functionapp config appsettings delete --name $APP_NAME --resource-group $RG --setting-names AzureWebJobsStorage
```

Update `WebStorage` connection:

```bash
CLIENT_ID=$(az identity show --name $USER_IDENTITY_NAME --resource-group $RG --query 'clientId' -o tsv)
az functionapp config appsettings set --name $APP_NAME --resource-group $RG --settings \
AzureWebJobsStorage__accountName=$STORAGE AzureWebJobsStorage__credential=managedidentity AzureWebJobsStorage__clientId=$CLIENT_ID
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
