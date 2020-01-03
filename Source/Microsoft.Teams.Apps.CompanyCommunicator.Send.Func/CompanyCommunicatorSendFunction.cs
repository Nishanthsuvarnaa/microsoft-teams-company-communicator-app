// <copyright file="CompanyCommunicatorSendFunction.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace Microsoft.Teams.Apps.CompanyCommunicator.Send.Func
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Table;
    using Microsoft.Azure.ServiceBus;
    using Microsoft.Azure.WebJobs;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories.NotificationData;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories.SentNotificationData;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Repositories.UserData;
    using Microsoft.Teams.Apps.CompanyCommunicator.Common.Services.MessageQueue;
    using Newtonsoft.Json;

    /// <summary>
    /// Azure Function App triggered by messages from a Service Bus queue
    /// Used for sending messages from the bot.
    /// </summary>
    public class CompanyCommunicatorSendFunction
    {
        /// <summary>
        /// This is set to 10 because the default maximum delivery count from the service bus
        /// message queue before the service bus will automatically put the message in the Dead Letter
        /// Queue is 10.
        /// </summary>
        private static readonly int MaxDeliveryCountForDeadLetter = 10;

        // Set as static so all instances can share the same access token.
        private static string botAccessToken = null;
        private static DateTime? botAccessTokenExpiration = null;

        private readonly IConfiguration configuration;
        private readonly HttpClient httpClient;
        private readonly SendingNotificationDataRepository sendingNotificationDataRepository;
        private readonly GlobalSendingNotificationDataRepository globalSendingNotificationDataRepository;
        private readonly UserDataRepository userDataRepository;
        private readonly SentNotificationDataRepository sentNotificationDataRepository;
        private readonly SendQueue sendQueue;
        private readonly DataQueue dataQueue;

        /// <summary>
        /// Initializes a new instance of the <see cref="CompanyCommunicatorSendFunction"/> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <param name="httpClient">The http client.</param>
        /// <param name="sendingNotificationDataRepository">The sending notification data repository.</param>
        /// <param name="globalSendingNotificationDataRepository">The global sending notification data repository.</param>
        /// <param name="userDataRepository">The user data repository.</param>
        /// <param name="sentNotificationDataRepository">The sent notification data repository.</param>
        /// <param name="sendQueue">The send queue.</param>
        /// <param name="dataQueue">The data queue.</param>
        public CompanyCommunicatorSendFunction(
            IConfiguration configuration,
            HttpClient httpClient,
            SendingNotificationDataRepository sendingNotificationDataRepository,
            GlobalSendingNotificationDataRepository globalSendingNotificationDataRepository,
            UserDataRepository userDataRepository,
            SentNotificationDataRepository sentNotificationDataRepository,
            SendQueue sendQueue,
            DataQueue dataQueue)
        {
            this.configuration = configuration;
            this.httpClient = httpClient;
            this.sendingNotificationDataRepository = sendingNotificationDataRepository;
            this.globalSendingNotificationDataRepository = globalSendingNotificationDataRepository;
            this.userDataRepository = userDataRepository;
            this.sentNotificationDataRepository = sentNotificationDataRepository;
            this.sendQueue = sendQueue;
            this.dataQueue = dataQueue;
        }

        /// <summary>
        /// Azure Function App triggered by messages from a Service Bus queue
        /// Used for sending messages from the bot.
        /// </summary>
        /// <param name="myQueueItem">The Service Bus queue item.</param>
        /// <param name="deliveryCount">The deliver count.</param>
        /// <param name="enqueuedTimeUtc">The enqueued time.</param>
        /// <param name="messageId">The message ID.</param>
        /// <param name="log">The logger.</param>
        /// <param name="context">The execution context.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        [FunctionName("CompanyCommunicatorSendFunction")]
        public async Task Run(
            [ServiceBusTrigger(
                SendQueue.QueueName,
                Connection = SendQueue.ServiceBusConnectionConfigurationKey)]
            string myQueueItem,
            int deliveryCount,
            DateTime enqueuedTimeUtc,
            string messageId,
            ILogger log,
            ExecutionContext context)
        {
            log.LogInformation($"C# ServiceBus queue trigger function processed message: {myQueueItem}");

            var messageContent = JsonConvert.DeserializeObject<ServiceBusSendQueueMessageContent>(myQueueItem);

            var totalNumberOfThrottles = 0;

            try
            {
                // If the configuration value is not set, set the default to 1.
                var maxNumberOfAttempts = this.configuration.GetValue<int>("MaxNumberOfAttempts", 1);

                // Check the shared access token. If it is not present or is invalid, then fetch a new one.
                if (CompanyCommunicatorSendFunction.botAccessToken == null
                    || CompanyCommunicatorSendFunction.botAccessTokenExpiration == null
                    || DateTime.UtcNow > CompanyCommunicatorSendFunction.botAccessTokenExpiration)
                {
                    await this.FetchTokenAsync(this.configuration, this.httpClient);
                }

                // Fetch the current sending notification. This is where data about what is being sent is stored.
                var getActiveNotificationEntityTask = this.sendingNotificationDataRepository.GetAsync(
                    PartitionKeyNames.NotificationDataTable.SendingNotificationsPartition,
                    messageContent.NotificationId);

                // Fetch the current global sending notification data. This is where data about the overall systems
                // status is stored e.g. is everything in a delayed state because the bot is being throttled.
                var getGlobalSendingNotificationDataEntityTask = this.globalSendingNotificationDataRepository
                    .GetGlobalSendingNotificationDataEntity();

                var incomingUserDataEntity = messageContent.UserDataEntity;
                var incomingConversationId = incomingUserDataEntity.ConversationId;

                // If the incoming payload does not have a conversationId, fetch the data for that user.
                var getUserDataEntityTask = string.IsNullOrWhiteSpace(incomingConversationId)
                    ? this.userDataRepository.GetAsync(
                        PartitionKeyNames.UserDataTable.UserDataPartition,
                        incomingUserDataEntity.AadId)
                    : Task.FromResult<UserDataEntity>(null);

                await Task.WhenAll(getActiveNotificationEntityTask, getGlobalSendingNotificationDataEntityTask, getUserDataEntityTask);

                var activeNotificationEntity = await getActiveNotificationEntityTask;
                var globalSendingNotificationDataEntity = await getGlobalSendingNotificationDataEntityTask;
                var userDataEntity = await getUserDataEntityTask;

                // If the incoming conversationId was not present, attempt to use the conversationId stored for
                // that user.
                // NOTE: It is possible that that user's data has not been stored in the user data repository.
                // If this is the case, then the conversation will have to be created for that user.
                var conversationId = string.IsNullOrWhiteSpace(incomingConversationId)
                    ? userDataEntity?.ConversationId
                    : incomingConversationId;

                // Initiate tasks that will be run in parallel if the step is required.
                Task saveUserDataEntityTask = Task.CompletedTask;
                Task saveSentNotificationDataTask = Task.CompletedTask;
                Task setDelayTimeAndSendDelayedRetryTask = Task.CompletedTask;

                // If the overall system is in a throttled state and needs to be delayed,
                // add the message back on the queue with a delay.
                if (globalSendingNotificationDataEntity?.SendRetryDelayTime != null
                    && DateTime.UtcNow < globalSendingNotificationDataEntity.SendRetryDelayTime)
                {
                    await this.SendDelayedRetryOfMessageToSendFunction(this.configuration, messageContent);

                    return;
                }

                // If the conversationId is known, the conversation does not need to be created.
                // If it a conversationId for a team, then nothing more needs to be done.
                // If it is a conversationId for a user, it is possible that the incoming user data has
                // more information than what is currently stored in the user data repository. Because of this,
                // save/update that user's information.
                if (!string.IsNullOrWhiteSpace(conversationId))
                {
                    // Set the conversationId so it is not removed from the user data repository on the update.
                    incomingUserDataEntity.ConversationId = conversationId;

                    // Verify that the conversationId is for a user (starting with 19: means it is for a team's
                    // General channel).
                    if (!conversationId.StartsWith("19:"))
                    {
                        incomingUserDataEntity.PartitionKey = PartitionKeyNames.UserDataTable.UserDataPartition;
                        incomingUserDataEntity.RowKey = incomingUserDataEntity.AadId;

                        var operation = TableOperation.InsertOrMerge(incomingUserDataEntity);

                        saveUserDataEntityTask = this.userDataRepository.Table.ExecuteAsync(operation);
                    }
                }
                else
                {
                    /*
                     * Falling into this block means that the message is meant for a user, but a conversationId
                     * is not known for that user (most likely "send to a team's members" option was selected
                     * as the audience). Because of this, the conversation needs to be created and that
                     * conversationId needs to be stored for that user.
                     */

                    var isCreateConversationThrottled = false;

                    // Loop through attempts to try and create the conversation for the user.
                    for (int i = 0; i < maxNumberOfAttempts; i++)
                    {
                        // Send a POST request to the correct URL with a valid access token and the
                        // correct message body.
                        var createConversationUrl = $"{incomingUserDataEntity.ServiceUrl}v3/conversations";
                        using (var requestMessage = new HttpRequestMessage(HttpMethod.Post, createConversationUrl))
                        {
                            requestMessage.Headers.Authorization = new AuthenticationHeaderValue(
                                "Bearer",
                                CompanyCommunicatorSendFunction.botAccessToken);

                            var payloadString = "{\"bot\": { \"id\": \"28:" + this.configuration["MicrosoftAppId"] + "\"},\"isGroup\": false, \"tenantId\": \"" + incomingUserDataEntity.TenantId + "\", \"members\": [{\"id\": \"" + incomingUserDataEntity.UserId + "\"}]}";
                            requestMessage.Content = new StringContent(payloadString, Encoding.UTF8, "application/json");

                            using (var sendResponse = await this.httpClient.SendAsync(requestMessage))
                            {
                                // If the conversation was created successfully, parse out the conversationId,
                                // store it for that user in the user data repository and place that
                                // conversationId for use when sending the notification to the user.
                                if (sendResponse.StatusCode == HttpStatusCode.Created)
                                {
                                    var jsonResponseString = await sendResponse.Content.ReadAsStringAsync();
                                    dynamic resp = JsonConvert.DeserializeObject(jsonResponseString);

                                    incomingUserDataEntity.PartitionKey = PartitionKeyNames.UserDataTable.UserDataPartition;
                                    incomingUserDataEntity.RowKey = incomingUserDataEntity.AadId;
                                    incomingUserDataEntity.ConversationId = resp.id;

                                    var operation = TableOperation.InsertOrMerge(incomingUserDataEntity);

                                    saveUserDataEntityTask = this.userDataRepository.Table.ExecuteAsync(operation);

                                    isCreateConversationThrottled = false;

                                    break;
                                }
                                else if (sendResponse.StatusCode == HttpStatusCode.TooManyRequests)
                                {
                                    // If the request was throttled, set the flag for if the maximum number of attempts
                                    // is reached, increment the count of the number of throttles to be stored
                                    // later, and if the maximum number of throttles has not been reached, delay
                                    // for a bit of time to attempt the request again.
                                    isCreateConversationThrottled = true;

                                    totalNumberOfThrottles++;

                                    // Do not delay if already attempted the maximum number of attempts.
                                    if (i != maxNumberOfAttempts - 1)
                                    {
                                        var random = new Random();
                                        await Task.Delay(random.Next(500, 1500));
                                    }
                                }
                                else
                                {
                                    // If in this block, then an error has occurred with the service.
                                    // Save the relevant information and do not attempt the request again.
                                    await this.SaveSentNotificationData(
                                        messageContent.NotificationId,
                                        incomingUserDataEntity.AadId,
                                        totalNumberOfThrottles,
                                        isStatusCodeFromCreateConversation: true,
                                        statusCode: sendResponse.StatusCode);

                                    return;
                                }
                            }
                        }
                    }

                    // If the request was attempted the maximum number of attempts and received
                    // all throttling responses, then set the overall delay time for the system so all
                    // other calls will be delayed and add the message back to the queue with a delay to be
                    // attempted later.
                    if (isCreateConversationThrottled)
                    {
                        await this.SetDelayTimeAndSendDelayedRetry(this.configuration, messageContent);

                        return;
                    }
                }

                var isSendMessageThrottled = false;

                // At this point, the conversationId of where to send the message should be known.
                // Loop through attempts to try and send the notification.
                for (int i = 0; i < maxNumberOfAttempts; i++)
                {
                    // Send a POST request to the correct URL with a valid access token and the
                    // correct message body.
                    var conversationUrl = $"{incomingUserDataEntity.ServiceUrl}v3/conversations/{incomingUserDataEntity.ConversationId}/activities";
                    using (var requestMessage = new HttpRequestMessage(HttpMethod.Post, conversationUrl))
                    {
                        requestMessage.Headers.Authorization = new AuthenticationHeaderValue(
                            "Bearer",
                            CompanyCommunicatorSendFunction.botAccessToken);

                        var attachmentJsonString = JsonConvert.DeserializeObject(activeNotificationEntity.Content);
                        var messageString = "{ \"type\": \"message\", \"attachments\": [ { \"contentType\": \"application/vnd.microsoft.card.adaptive\", \"content\": " + attachmentJsonString + " } ] }";
                        requestMessage.Content = new StringContent(messageString, Encoding.UTF8, "application/json");

                        using (var sendResponse = await this.httpClient.SendAsync(requestMessage))
                        {
                            // If the notification was sent successfully, store the data about the
                            // successful request.
                            if (sendResponse.StatusCode == HttpStatusCode.Created)
                            {
                                log.LogInformation("MESSAGE SENT SUCCESSFULLY");

                                saveSentNotificationDataTask = this.SaveSentNotificationData(
                                    messageContent.NotificationId,
                                    incomingUserDataEntity.AadId,
                                    totalNumberOfThrottles,
                                    isStatusCodeFromCreateConversation: false,
                                    statusCode: sendResponse.StatusCode);

                                isSendMessageThrottled = false;

                                break;
                            }
                            else if (sendResponse.StatusCode == HttpStatusCode.TooManyRequests)
                            {
                                // If the request was throttled, set the flag for if the maximum number of attempts
                                // is reached, increment the count of the number of throttles to be stored
                                // later, and if the maximum number of throttles has not been reached, delay
                                // for a bit of time to attempt the request again.
                                log.LogError("MESSAGE THROTTLED");

                                isSendMessageThrottled = true;

                                totalNumberOfThrottles++;

                                // Do not delay if already attempted the maximum number of attempts.
                                if (i != maxNumberOfAttempts - 1)
                                {
                                    var random = new Random();
                                    await Task.Delay(random.Next(500, 1500));
                                }
                            }
                            else
                            {
                                // If in this block, then an error has occurred with the service.
                                // Save the relevant information and do not attempt the request again.
                                log.LogError($"MESSAGE FAILED: {sendResponse.StatusCode}");

                                saveSentNotificationDataTask = this.SaveSentNotificationData(
                                    messageContent.NotificationId,
                                    incomingUserDataEntity.AadId,
                                    totalNumberOfThrottles,
                                    isStatusCodeFromCreateConversation: false,
                                    statusCode: sendResponse.StatusCode);

                                await Task.WhenAll(saveUserDataEntityTask, saveSentNotificationDataTask);

                                return;
                            }
                        }
                    }
                }

                // If the request was attempted the maximum number of attempts and received
                // all throttling responses, then set the overall delay time for the system so all
                // other calls will be delayed and add the message back to the queue with a delay to be
                // attempted later.
                if (isSendMessageThrottled)
                {
                    setDelayTimeAndSendDelayedRetryTask =
                        this.SetDelayTimeAndSendDelayedRetry(this.configuration, messageContent);
                }

                await Task.WhenAll(
                    saveUserDataEntityTask,
                    saveSentNotificationDataTask,
                    setDelayTimeAndSendDelayedRetryTask);
            }
            catch (Exception e)
            {
                /*
                 * If in this block, then an exception was thrown. If the function throws an exception
                 * then the service bus message will be placed back on the queue. If this process has
                 * been done enough times and the message has been attempted to be delivered more than
                 * its allowed delivery count, then the message is placed on the dead letter queue of
                 * the service bus. For each attempt that did not result with the message being placed
                 * on the dead letter queue, set the status to be stored as HttpStatusCode.Continue. If
                 * the maximum delivery count has been reached and the message will be place on the
                 * dead letter queue, then set the status to be stored as HttpStatusCode.InternalServerError.
                 */

                log.LogError(e, $"ERROR: {e.Message}, {e.GetType()}");

                var statusCodeToStore = HttpStatusCode.Continue;
                if (deliveryCount >= CompanyCommunicatorSendFunction.MaxDeliveryCountForDeadLetter)
                {
                    statusCodeToStore = HttpStatusCode.InternalServerError;
                }

                await this.SaveSentNotificationData(
                    messageContent.NotificationId,
                    messageContent.UserDataEntity.AadId,
                    totalNumberOfThrottles,
                    isStatusCodeFromCreateConversation: false,
                    statusCode: statusCodeToStore);

                throw e;
            }
        }

        private async Task SaveSentNotificationData(
            string notificationId,
            string aadId,
            int totalNumberOfThrottles,
            bool isStatusCodeFromCreateConversation,
            HttpStatusCode statusCode)
        {
            var updatedSentNotificationDataEntity = new SentNotificationDataEntity
            {
                PartitionKey = notificationId,
                RowKey = aadId,
                AadId = aadId,
                TotalNumberOfThrottles = totalNumberOfThrottles,
                SentDate = DateTime.UtcNow,
                IsStatusCodeFromCreateConversation = isStatusCodeFromCreateConversation,
                StatusCode = (int)statusCode,
            };

            if (statusCode == HttpStatusCode.Created)
            {
                updatedSentNotificationDataEntity.DeliveryStatus = SentNotificationDataEntity.Succeeded;
            }
            else if (statusCode == HttpStatusCode.TooManyRequests)
            {
                updatedSentNotificationDataEntity.DeliveryStatus = SentNotificationDataEntity.Throttled;
            }
            else
            {
                updatedSentNotificationDataEntity.DeliveryStatus = SentNotificationDataEntity.Failed;
            }

            var operation = TableOperation.InsertOrMerge(updatedSentNotificationDataEntity);

            await this.sentNotificationDataRepository.Table.ExecuteAsync(operation);
        }

        private async Task FetchTokenAsync(
            IConfiguration configuration,
            HttpClient httpClient)
        {
            var values = new Dictionary<string, string>
                {
                    { "grant_type", "client_credentials" },
                    { "client_id", configuration["MicrosoftAppId"] },
                    { "client_secret", configuration["MicrosoftAppPassword"] },
                    { "scope", "https://api.botframework.com/.default" },
                };
            var content = new FormUrlEncodedContent(values);

            using (var tokenResponse = await httpClient.PostAsync("https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token", content))
            {
                if (tokenResponse.StatusCode == HttpStatusCode.OK)
                {
                    var accessTokenContent = await tokenResponse.Content.ReadAsAsync<AccessTokenResponse>();

                    CompanyCommunicatorSendFunction.botAccessToken = accessTokenContent.AccessToken;

                    var expiresInSeconds = 121;

                    // If parsing fails, out variable is set to 0, so need to set the default
                    if (!int.TryParse(accessTokenContent.ExpiresIn, out expiresInSeconds))
                    {
                        expiresInSeconds = 121;
                    }

                    // Remove two minutes in order to have a buffer amount of time.
                    CompanyCommunicatorSendFunction.botAccessTokenExpiration = DateTime.UtcNow + TimeSpan.FromSeconds(expiresInSeconds - 120);
                }
                else
                {
                    throw new Exception("Error fetching bot access token.");
                }
            }
        }

        private async Task SetDelayTimeAndSendDelayedRetry(
            IConfiguration configuration,
            ServiceBusSendQueueMessageContent queueMessageContent)
        {
            // If the configuration value is not set, set the default to 11.
            var sendRetryDelayNumberOfMinutes = configuration.GetValue<int>("SendRetryDelayNumberOfMinutes", 11);

            // Shorten this time by 15 seconds to ensure that when the delayed retry message is taken off of the queue
            // the Send Retry Delay Time will be earlier and will not block it
            var sendRetryDelayTime = DateTime.UtcNow + TimeSpan.FromMinutes(sendRetryDelayNumberOfMinutes - 0.25);

            var globalSendingNotificationDataEntity = new GlobalSendingNotificationDataEntity
            {
                SendRetryDelayTime = sendRetryDelayTime,
            };

            await this.globalSendingNotificationDataRepository
                .SetGlobalSendingNotificationDataEntity(globalSendingNotificationDataEntity);

            await this.SendDelayedRetryOfMessageToSendFunction(configuration, queueMessageContent);
        }

        private async Task SendDelayedRetryOfMessageToSendFunction(
            IConfiguration configuration,
            ServiceBusSendQueueMessageContent queueMessageContent)
        {
            // If the configuration value is not set, set the default to 11.
            var sendRetryDelayNumberOfMinutes = configuration.GetValue<int>("SendRetryDelayNumberOfMinutes", 11);

            var messageBody = JsonConvert.SerializeObject(queueMessageContent);
            var serviceBusMessage = new Message(Encoding.UTF8.GetBytes(messageBody));
            serviceBusMessage.ScheduledEnqueueTimeUtc = DateTime.UtcNow + TimeSpan.FromMinutes(sendRetryDelayNumberOfMinutes);

            await this.sendQueueServiceBusMessageSender.SendAsync(serviceBusMessage);
        }

        private class ServiceBusSendQueueMessageContent
        {
            public string NotificationId { get; set; }

            // This can be a team.id
            public UserDataEntity UserDataEntity { get; set; }
        }

        private class AccessTokenResponse
        {
            [JsonProperty("token_type")]
            public string TokenType { get; set; }

            [JsonProperty("expires_in")]
            public string ExpiresIn { get; set; }

            [JsonProperty("ext_expires_in")]
            public string ExtExpiresIn { get; set; }

            [JsonProperty("access_token")]
            public string AccessToken { get; set; }
        }
    }
}
