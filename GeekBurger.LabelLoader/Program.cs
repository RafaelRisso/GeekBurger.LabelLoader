using System;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Azure.Management.ServiceBus.Fluent;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Azure.ServiceBus;
using Newtonsoft.Json;
using System.Text;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using System.Threading;
using Microsoft.Extensions.Configuration;

namespace GeekBurger.LabelLoader
{
    class Program
    {
        public static string[] blacklist = new string[] { "ingredients", "processed in a facility that handles", "products", "allergens", "contains" };
        public const string AndWithSpace = " and ";
        public const string CommaWithSpace = " , ";
        static string subscriptionKey = "df73d9d6a2b94ef683730650309ab2bc";
        static string endpoint = "https://labelloader.cognitiveservices.azure.com/";
        static string uriBase = endpoint + "vision/v2.1/ocr";


        private static ServiceBusConfiguration config;
        private static IServiceBusNamespace serviceBus;
        private static readonly List<Message> messages = new List<Message>();
        private static Task lastTask;

        static async Task Main()
        {
            // Build configuration
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", true, true)
                .Build();

            config = builder.GetSection("serviceBus").Get<ServiceBusConfiguration>();


            var credentials = SdkContext.AzureCredentialsFactory
                .FromServicePrincipal(config.ClientId,
                    config.ClientSecret,
                    config.TenantId,
                    AzureEnvironment.AzureGlobalCloud);

            var serviceBusManager = ServiceBusManager
                .Authenticate(credentials, config.SubscriptionId);

            serviceBus = serviceBusManager.Namespaces
                .GetByResourceGroup(config.ResourceGroup,
                    config.NamespaceName);


            Console.WriteLine("Reconhecimento de Rotulos:");
            Console.Write("Insira o caminho completo da imagem e de enter");
            string imageFilePath = Console.ReadLine();

            if (File.Exists(imageFilePath))
            {

                Console.WriteLine("\n Aguarde os resultados.\n");
                await MakeOCRRequest(imageFilePath);
            }
            else
            {
                Console.WriteLine("\nCaminho inválido");
            }
            Console.WriteLine("\nPressione ENTER para sair...");
            Console.ReadLine();
        }


        static async Task MakeOCRRequest(string imageFilePath)
        {
            try
            {
                HttpClient client = new HttpClient();


                client.DefaultRequestHeaders.Add(
                    "Ocp-Apim-Subscription-Key", subscriptionKey);

                string requestParameters = "language=unk&detectOrientation=true";
                string uri = uriBase + "?" + requestParameters;

                HttpResponseMessage response;


                byte[] byteData = GetImageAsByteArray(imageFilePath);


                using (ByteArrayContent content = new ByteArrayContent(byteData))
                {

                    content.Headers.ContentType =
                        new MediaTypeHeaderValue("application/octet-stream");


                    response = await client.PostAsync(uri, content);
                }


                string contentString = await response.Content.ReadAsStringAsync();


                Console.WriteLine("\nResposta:\n\n{0}\n",
                    JToken.Parse(contentString).ToString());

                // resultado mockado
                var labelImageAdded = new LabelImageAdded();

                EnsureTopicIsCreated("LabelImageAdded", "LabelImageAdded_Topic");

                messages.Add(new Message
                {
                    Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new LabelImageAdded())),
                    MessageId = Guid.NewGuid().ToString(),
                    Label = "LabelImageAdded"
                });

                SendMessagesAsync("LabelImageAdded");

            }
            catch (Exception e)
            {
                Console.WriteLine("\n" + e.Message);
            }

            static byte[] GetImageAsByteArray(string imageFilePath)
            {

                using (FileStream fileStream =
                    new FileStream(imageFilePath, FileMode.Open, FileAccess.Read))
                {
                    BinaryReader binaryReader = new BinaryReader(fileStream);
                    return binaryReader.ReadBytes((int)fileStream.Length);
                }
            }
        }

        public static void EnsureTopicIsCreated(string TopicName, string SubscriptionName)
        {
            if (!serviceBus.Topics.List()
                  .Any(t => t.Name
                  .Equals(TopicName, StringComparison.InvariantCultureIgnoreCase)))
            {
                serviceBus.Topics
                    .Define(TopicName)
                    .WithSizeInMB(1024)
                    .Create();
            }

            var topic = serviceBus.Topics.GetByName(TopicName);

            if (topic.Subscriptions.List()
              .Any(subscription => subscription.Name
              .Equals(SubscriptionName,
                     StringComparison.InvariantCultureIgnoreCase)))
            {
                topic.Subscriptions.DeleteByName(SubscriptionName);
            }

            topic.Subscriptions
                .Define(SubscriptionName)
                .Create();
        }

        public static async void SendMessagesAsync(string topic)
        {
            try
            {
                if (lastTask != null && !lastTask.IsCompleted)
                    return;

                var topicClient = new TopicClient(config.ConnectionString, topic);

                lastTask = SendAsync(topicClient);

                await lastTask;

                var closeTask = topicClient.CloseAsync();
                await closeTask;
                HandleException(closeTask);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public static async Task SendAsync(TopicClient topicClient)
        {
            var tries = 0;
            while (true)
            {
                if (messages.Count <= 0)
                    break;

                Message message;
                lock (messages)
                {
                    message = messages.FirstOrDefault();
                }

                var sendTask = topicClient.SendAsync(message);
                await sendTask;
                var success = HandleException(sendTask);

                if (!success)
                    Thread.Sleep(10000 * (tries < 60 ? tries++ : tries));
                else
                    messages.Remove(message);

            }
        }

        public static bool HandleException(Task task)
        {
            if (task.Exception == null || task.Exception.InnerExceptions.Count == 0) return true;

            task.Exception.InnerExceptions.ToList().ForEach(innerException =>
            {
                Console.WriteLine($"Error in SendAsync task: {innerException.Message}. Details:{innerException.StackTrace} ");

                if (innerException is ServiceBusCommunicationException)
                    Console.WriteLine("Connection Problem with Host.");
            });

            return false;
        }
    }
}