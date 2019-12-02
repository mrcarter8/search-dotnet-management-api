﻿using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using System.Threading;

using Microsoft.Azure.Management.Search;
using Microsoft.Azure.Management.Search.Models;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.ResourceManager.Models;
using Sku = Microsoft.Azure.Management.Search.Models.Sku;

using Microsoft.Rest.Azure.Authentication;


namespace ManagementAPI
{
    // In order to use the Azure Resource Manager API you need to prepare the target subscription. This is discussed
    // in detail here:
    // http://msdn.microsoft.com/en-us/library/azure/dn790557.aspx

    class Program
    {
        public static void Main(string[] args)
        {
            IConfigurationBuilder builder = new ConfigurationBuilder().AddJsonFile("appsettings.json");
            IConfigurationRoot configuration = builder.Build();

            // You can obtain this information from the Azure management portal. The instructions in the link above 
            // include details for this as well.
            var tenantId = configuration["TenantId"];
            var clientId = configuration["ClientId"];
            var clientSecret = configuration["ClientSecret"];
            var subscriptionId = configuration["SubscriptionId"];

            // This is the return URL you configure during AD client application setup. For this type of apps (non-web apps)
            // you can set this to something like http://localhost/testapp. The important thing is that the URL here and the
            // URL in AD configuration match.
            //Uri RedirectUrl = null;

            if (new List<string> { tenantId, clientId, clientSecret, subscriptionId }.Any(i => String.IsNullOrEmpty(i)))
            {
                Console.WriteLine("Please provide values for tenantId, clientId, secret and subscriptionId.");
            }
            else
            {
                RunSample(tenantId, clientId, clientSecret, subscriptionId).Wait();
            }
        }

        public static async Task RunSample(string tenantId, string clientId, string clientSecret, string subscriptionId)
        {
            // Build the service credentials and Azure Resource Manager clients
            var creds = await ApplicationTokenProvider.LoginSilentAsync(tenantId, clientId, clientSecret);

            var subscriptionClient = new SubscriptionClient(creds);
            var resourceClient = new ResourceManagementClient(creds);
            resourceClient.SubscriptionId = subscriptionId;
            var searchClient = new SearchManagementClient(creds);
            searchClient.SubscriptionId = subscriptionId;

            var _random = new Random();

            // Get some general subscription information, not Azure Search specific
            var subscription = await subscriptionClient.Subscriptions.GetAsync(subscriptionId);
            DisplaySubscriptionDetails(subscription);

            // Register the Azure Search resource provider with the subscription. In the Azure Resource Manager model, you need
            // to register a resource provider in a subscription before you can use it. 
            // You only need to do this once per subscription/per resource provider.
            // More details here:
            // http://msdn.microsoft.com/en-us/library/azure/dn790548.aspx
            var provider = resourceClient.Providers.Register("Microsoft.Search");
            DisplayProviderDetails(provider);

            // List all search services in the subscription by resource group.  How to list resource groups is detailed here:
            // http://msdn.microsoft.com/en-us/library/azure/dn790529.aspx4
            var groups = await resourceClient.ResourceGroups.ListAsync();

            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("List all search services in the subscription by resource group");
            Console.WriteLine("----------------------------------------------------");

            foreach (var group in groups)
            {
                var resources = await resourceClient.Resources.ListAsync("resourceGroup eq '" + group.Name + "' and resourceType eq 'Microsoft.Search/searchServices'");
                if (resources.Count() > 0)
                Console.WriteLine("resourceGroup: {0}", group.Name);
                {
                    foreach (var resource in resources)
                    {
                        Console.WriteLine("   service name: {0}, sku: {1}, location: {2}", resource.Name, resource.Sku.Name, resource.Location);
                    }
                }
            }
            Console.WriteLine();

            // Create a new free search service called "sample#" (# is a random number, to make it less likely to have collisions)
            // NOTE: if you already have a free service in this subscription this operation will fail
            string newServiceName = "sampleservice" + _random.Next(0, 1000000).ToString();
            string newGroupName = "samplegroup" + _random.Next(0, 1000000).ToString();
            await resourceClient.ResourceGroups.CreateOrUpdateAsync(newGroupName, new ResourceGroup { Location = "West US" });

            var newService = await searchClient.Services.CreateOrUpdateAsync(newGroupName, newServiceName, 
                new SearchService(){
                Location = "West US",
                Sku = new Sku() { Name = SkuName.Standard }, // use "standard" for standard services
                PartitionCount = 1,
                ReplicaCount = 1
                });

            // Wait for service provisioning to complete
            while (newService.ProvisioningState == ProvisioningState.Provisioning)
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));

                // Retrieve service definition by ResourceGroup name and Service Name
                newService = await searchClient.Services.GetAsync(newGroupName, newService.Name);
            }

            // Retrieve service admin API keys
            AdminKeyResult adminKeys = searchClient.AdminKeys.Get(newGroupName, newService.Name);

            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Service admin API keys");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Primary admin API key: {0}", adminKeys.PrimaryKey);
            Console.WriteLine("Secondary admin API key: {0}", adminKeys.SecondaryKey);
            Console.WriteLine();

            // Regenerate admin API keys
            // (use /primary to regenerate the primary admin API key)
            AdminKeyResult newPrimary = searchClient.AdminKeys.Regenerate(newGroupName, newService.Name, AdminKeyKind.Primary);
            AdminKeyResult newSecondary = searchClient.AdminKeys.Regenerate(newGroupName, newService.Name, AdminKeyKind.Secondary);

            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Regenerate admin API keys");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("New primary admin API key: {0}", newPrimary.PrimaryKey);
            Console.WriteLine("New secondary admin API key: {0}", newSecondary.SecondaryKey);
            Console.WriteLine();

            // Create a new query API key
            QueryKey newQueryKey = searchClient.QueryKeys.Create(newGroupName, newService.Name, "new query key");

            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Create a new query API key");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("New query API key: {0}", newQueryKey.Key);
            Console.WriteLine();

            // Retrieve query API key
            searchClient.QueryKeys.Create(newGroupName, newService.Name, "new query key2");
            QueryKey getQueryKey = (await searchClient.QueryKeys.ListBySearchServiceAsync(newGroupName, newService.Name)).Where(s => s.Name == "new query key2").First();

            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Retrieve query API key");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Retrieved query API key: {0}", getQueryKey.Key);
            Console.WriteLine();

            // Delete a query API key by name
            await searchClient.QueryKeys.DeleteAsync(newGroupName, newService.Name, "new query key2");

            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Deleted query API key by name");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine();

            // Scale up service to 2 partitions and 2 replicas
            // NOTE: this will fail unless you change the service creation code above to make it a "standard" service and wait until it's provisioned
            newService = searchClient.Services.Update(newGroupName, newService.Name, new SearchService() { ReplicaCount = 2, PartitionCount = 2 });

            // Wait for provisioning to complete
            while (newService.ProvisioningState == ProvisioningState.Provisioning)
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));

                // Retrieve service definition by ResourceGroup name and Service Name
                newService = await searchClient.Services.GetAsync(newGroupName, newService.Name);
            }

            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Scale up service to 2 partitions and 2 replicas");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Partition Count: {0}", newService.PartitionCount);
            Console.WriteLine("Replica Count: {0}", newService.ReplicaCount);
            Console.WriteLine();

            // Scale back down to 1 replica x 1 partition
            newService = searchClient.Services.Update(newGroupName, newService.Name, new SearchService() { ReplicaCount = 1, PartitionCount = 1 });

            // Wait for provisioning to complete
            while (newService.ProvisioningState == ProvisioningState.Provisioning)
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));

                // Retrieve service definition by ResourceGroup name and Service Name
                newService = await searchClient.Services.GetAsync(newGroupName, newService.Name);
            }

            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Scale back down to 1 replica x 1 partition");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Partition Count: {0}", newService.PartitionCount);
            Console.WriteLine("Replica Count: {0}", newService.ReplicaCount);
            Console.WriteLine();

            // Delete search service
            await searchClient.Services.DeleteAsync(newGroupName, newService.Name);
            if (searchClient.Services.ListByResourceGroup(newGroupName).Count() == 0)
            {
                Console.WriteLine("----------------------------------------------------");
                Console.WriteLine("Search service successfully deleted");
                Console.WriteLine("----------------------------------------------------");
                Console.WriteLine();
            }
        }

        private static void DisplaySubscriptionDetails(Subscription sub)
        {
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Subscription Details");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine();
            Console.WriteLine("id: {0}", sub.Id);
            Console.WriteLine("subscriptionId: {0}", sub.SubscriptionId);
            Console.WriteLine("displayName: {0}", sub.DisplayName);
            Console.WriteLine("state: {0}", sub.State);
            Console.WriteLine("subscriptionPolicies:");
            Console.WriteLine("   locationPlacementId: {0}", sub.SubscriptionPolicies.LocationPlacementId);
            Console.WriteLine("   quotaId: {0}", sub.SubscriptionPolicies.QuotaId);
            Console.WriteLine("   spendingLimit: {0}", sub.SubscriptionPolicies.SpendingLimit);
            Console.WriteLine();
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine();
        }

        private static void DisplayProviderDetails(Provider provider)
        {
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("Azure Search Provider Details");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine();
            Console.WriteLine("id: {0}", provider.Id);
            Console.WriteLine("namespace: {0}", provider.NamespaceProperty);
            Console.WriteLine("registrationPolicy: {0}", provider.RegistrationPolicy);
            Console.WriteLine("resourceTypes:");
            foreach (var rt in provider.ResourceTypes)
            {
                Console.WriteLine("   resourceType: {0}", rt.ResourceType);
                Console.WriteLine("      locations:");
                foreach (var loc in rt.Locations) Console.WriteLine("         {0}", loc);
                Console.WriteLine("      apiVersions:");
                foreach (var api in rt.ApiVersions) Console.WriteLine("         {0}", api);
            }
            Console.WriteLine("registrationState: {0}", provider.RegistrationState);
            Console.WriteLine();
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine();
        }
    }
}