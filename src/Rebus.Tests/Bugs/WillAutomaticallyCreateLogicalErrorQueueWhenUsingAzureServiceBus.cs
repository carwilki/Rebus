﻿using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using NUnit.Framework;
using Rebus.Bus;
using Rebus.Configuration;
using Rebus.AzureServiceBus;
using Rebus.Tests.Contracts.Transports.Factories;

namespace Rebus.Tests.Bugs
{
    [TestFixture, Category(TestCategories.Azure), Description("Test case verifies that the Azure Service Bus transport will ensure that a subscription exists for the error queue. Otherwise, messages could potentially be lost when moved to the error queue")]
    public class WillAutomaticallyCreateLogicalErrorQueueWhenUsingAzureServiceBus : FixtureBase
    {
        const string InputQueueName = "test_input";
        const string ErrorQueueName = "myCustomErrorQueue";
        const string RecognizableErrorMessage = "FAILLLLLLLL";
        List<IDisposable> stuffToDispose;
        BuiltinContainerAdapter adapter;
        ManualResetEvent resetEvent;

        static string ConnectionString
        {
            get { return AzureServiceBusMessageQueueFactory.ConnectionString; }
        }

        protected override void DoSetUp()
        {
            stuffToDispose = new List<IDisposable>();
            resetEvent = new ManualResetEvent(false);

            adapter = new BuiltinContainerAdapter();
            Configure.With(adapter)
                     .Transport(t => t.UseAzureServiceBus(ConnectionString, InputQueueName, ErrorQueueName))
                     .Events(e => e.PoisonMessage += (b, m, i) => resetEvent.Set())
                     .CreateBus()
                     .Start();
        }

        protected override void DoTearDown()
        {
            stuffToDispose.ForEach(s => s.Dispose());

            var namespaceManager = NamespaceManager.CreateFromConnectionString(ConnectionString);
            var topicDescription = namespaceManager.GetTopic(AzureServiceBusMessageQueue.TopicName);

            // clean up
            namespaceManager.DeleteSubscription(topicDescription.Path, InputQueueName);
            namespaceManager.DeleteSubscription(topicDescription.Path, ErrorQueueName);
        }

        [Test]
        public void YeahItDoes()
        {
            var deliveryAttempts = 0;
            adapter.Handle<string>(str =>
                {
                    Interlocked.Increment(ref deliveryAttempts);
                    throw new ApplicationException(RecognizableErrorMessage);
                });

            adapter.Bus.SendLocal("HELLO MY FRIEND!!!!!2222");

            var timeout = 5.Seconds();
            Assert.That(resetEvent.WaitOne(timeout), Is.True, "Did not receive PoisonMessage event within {0} timeout", timeout);
            Assert.That(deliveryAttempts, Is.EqualTo(5), "Expected 5 failed delivery attempts to have been made");

            // be sure that message has moved and everything
            Thread.Sleep(1.Seconds());

            using (var errorQueueClient = new AzureServiceBusMessageQueue(ConnectionString, ErrorQueueName))
            {
                var receivedTransportMessage = errorQueueClient.ReceiveMessage(new NoTransaction());

                Assert.That(receivedTransportMessage, Is.Not.Null);
            }
        }
    }
}