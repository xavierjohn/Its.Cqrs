// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Its.Domain.Serialization;
using Microsoft.Its.Domain.Testing;
using Microsoft.Its.Recipes;
using NUnit.Framework;
using Test.Domain.Ordering;

namespace Microsoft.Its.Domain.Tests
{
    [TestFixture]
    [DisableCommandAuthorization]
    public abstract class EventSourcedRepositoryTests
    {
        private CompositeDisposable disposables;

        protected abstract IEventSourcedRepository<TAggregate> CreateRepository<TAggregate>(
            Action onSave = null)
            where TAggregate : class, IEventSourced;

        [Test]
        public async Task Serialized_events_are_deserialized_in_their_correct_sequence_and_type()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();
            order
                .Apply(new AddItem
                {
                    ProductName = "Widget",
                    Price = 10m,
                    Quantity = 1
                })
                .Apply(new ChangeFufillmentMethod { FulfillmentMethod = FulfillmentMethod.DirectShip });

            await repository.Save(order);
            var rehydratedOrder = await repository.GetLatest(order.Id);

            rehydratedOrder.EventHistory.Count().Should().Be(3);
            rehydratedOrder.EventHistory.Skip(1).First().Should().BeOfType<Order.ItemAdded>();
            rehydratedOrder.EventHistory.Last().Should().BeOfType<Order.FulfillmentMethodSelected>();
        }

        [Test]
        public async Task When_an_aggregate_id_is_not_found_then_GetLatest_returns_null()
        {
            var aggregate = await CreateRepository<Order>().GetLatest(Any.Guid());

            aggregate.Should().BeNull();
        }

        [Test]
        public async Task GetVersion_does_not_pull_versions_after_the_specified_one()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();

            Enumerable.Range(1, 10).ForEach(i => order.Apply(new AddItem
            {
                ProductName = "Widget",
                Price = 10m,
                Quantity = i
            }));

            await repository.Save(order);
            var rehydratedOrder = await repository.GetVersion(order.Id, 4);

            rehydratedOrder.Version().Should().Be(4);
            rehydratedOrder.EventHistory.Count().Should().Be(4);
            rehydratedOrder.Items.First().Quantity.Should().Be(6);
        }

        [Test]
        public async Task GetAsOfDate_does_not_pull_events_after_the_specified_date()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();
            var startTime = DateTimeOffset.UtcNow;

            Enumerable.Range(1, 10).ForEach(i =>
            {
                Clock.Now = () => startTime.AddDays(i);
                order.Apply(new AddItem
                {
                    ProductName = "Widget",
                    Price = 10m,
                    Quantity = i
                });
            });

            await repository.Save(order);
            var rehydratedOrder = await repository.GetAsOfDate(order.Id, startTime.AddDays(3.1));

            rehydratedOrder.Version().Should().Be(4);
            rehydratedOrder.EventHistory.Count().Should().Be(4);
            rehydratedOrder.Items.First().Quantity.Should().Be(6);
        }

        [Test]
        public async Task Deserialized_events_are_used_to_rebuild_the_state_of_the_aggregate()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();
            order
                .Apply(new AddItem
                {
                    ProductName = "Widget",
                    Price = 10m,
                    Quantity = 2
                })
                .Apply(new AddItem
                {
                    ProductName = "Widget",
                    Price = 10m,
                    Quantity = 3
                });

            await repository.Save(order);
            var rehydratedOrder = await repository.GetLatest(order.Id);

            rehydratedOrder.Items.Single().Quantity.Should().Be(5);
        }

        [Test]
        public async Task When_Save_is_called_then_each_added_event_is_published()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();

            var publishedEvents = ListenToPublishedEvents();

            order
                .Apply(new AddItem
                {
                    ProductName = "Widget",
                    Price = 10m,
                    Quantity = 2
                })
                .Apply(new ChangeFufillmentMethod
                {
                    FulfillmentMethod = FulfillmentMethod.Delivery
                });
            await repository.Save(order);

            
            publishedEvents.Count
               .Should().Be(3);
            publishedEvents.Skip(1).First()
               .Should().BeOfType<Order.ItemAdded>();
            publishedEvents.Skip(1).Skip(1).First()
               .Should().BeOfType<Order.FulfillmentMethodSelected>();
        }

        [Test]
        public async Task When_Save_is_called_then_published_events_have_incrementing_sequence_ids()
        {
            // set up the repository so we're not starting from the beginning
            var order = new Order();
            var repository = CreateRepository<Order>();
            order.Apply(new AddItem
            {
                ProductName = "Widget",
                Price = 10m,
                Quantity = 2
            });
            await repository.Save(order);

            var publishedEvents = ListenToPublishedEvents();

            // apply 2 commands
            var order2 = await repository.GetLatest(order.Id);
            order2
                .Apply(new ChangeFufillmentMethod { FulfillmentMethod = FulfillmentMethod.Delivery })
                .Apply(new ChangeCustomerInfo { CustomerName = "Wanda" });
            await repository.Save(order2);

            publishedEvents.Count().Should().Be(2);
            publishedEvents.First().SequenceNumber.Should().Be(3);
            publishedEvents.Skip(1).First().SequenceNumber.Should().Be(4);
        }

        [Test]
        public abstract Task When_storage_fails_then_no_events_are_published();

        [Test]
        public async Task Concurrency_on_event_storage_is_optimistic_and_exceptions_have_informative_messages()
        {
            var repository = CreateRepository<Order>();
            var order = new Order()
                .Apply(new AddItem
                {
                    Price = .05m,
                    ProductName = "widget",
                    Quantity = 100
                })
                .Apply(new ChangeCustomerInfo
                {
                    CustomerName = "Wanda"
                });
            await repository.Save(order);

            var order2 = (await repository.GetLatest(order.Id))
                                   .Apply(new ChangeCustomerInfo
                                   {
                                       CustomerName = "Alice"
                                   });
            var order3 = (await repository.GetLatest(order.Id))
                                   .Apply(new ChangeCustomerInfo
                                   {
                                       CustomerName = "Bob"
                                   });

            await repository.Save(order2);

            repository.Invoking(r => r.Save(order3).Wait())
                      .ShouldThrow<ConcurrencyException>()
                      .And
                      .Message
                      .Should()
                      .Contain("Alice")
                      .And
                      .Contain("Bob")
                      .And
                      .Contain("Test.Domain.Ordering.Order+CustomerInfoChanged");
        }

        [Test]
        public abstract Task Events_that_cannot_be_deserialized_due_to_unknown_type_do_not_cause_sourcing_to_fail();

        [Test]
        public abstract Task Events_at_the_end_of_the_sequence_that_cannot_be_deserialized_due_to_unknown_type_do_not_cause_Version_to_be_incorrect();

        [Test]
        public abstract Task Events_that_cannot_be_deserialized_due_to_unknown_member_do_not_cause_sourcing_to_fail();

        protected abstract Task SaveEventsDirectly(params InMemoryStoredEvent[] events);

        protected abstract Task DeleteEventsFromEventStore(Guid aggregateId);

        [Test]
        public async Task Save_transfers_pending_events_to_event_history()
        {
            var order = new Order();
            var repository = CreateRepository<Order>();
            order.Apply(new AddItem { Price = 1m, ProductName = "Widget" });
            await repository.Save(order);

            order.EventHistory.Count().Should().Be(2);
        }

        [Test]
        public async Task After_Save_additional_events_continue_in_the_correct_sequence()
        {
            var order = new Order();
            var publishedEvents = ListenToPublishedEvents();
            var repository = CreateRepository<Order>();
            var addEvent = new Action(() =>
                                      order.Apply(new AddItem { Price = 1m, ProductName = "Widget" }));

            addEvent();
            await repository.Save(order);
            publishedEvents.Last().SequenceNumber.Should().Be(2);

            addEvent();
            addEvent();
            await repository.Save(order);
            publishedEvents.Last().SequenceNumber.Should().Be(4);

            addEvent();
            addEvent();
            addEvent();
            await repository.Save(order);
            publishedEvents.Last().SequenceNumber.Should().Be(7);
        }

        [Test]
        public async Task GetAggregate_can_be_used_when_no_aggregate_was_previously_sourced()
        {
            var order = new Order()
                .Apply(new ChangeCustomerInfo
                {
                    CustomerName = Any.FullName()
                });

            var repository = CreateRepository<Order>();
            await repository.Save(order);
            Order aggregate = null;
#pragma warning disable 618
            var consquenter = Consequenter.Create<Order.Placed>(e =>
#pragma warning restore 618
            {
#pragma warning disable 612
                aggregate = e.GetAggregate();
#pragma warning restore 612
            });
            consquenter.HaveConsequences(new Order.Placed
            {
                AggregateId = order.Id
            });

            aggregate.Id.Should().Be(order.Id);
        }

        [Test]
        public async Task GetLatest_can_return_an_aggregate_built_from_a_snapshot()
        {
            // arrange
            var snapshotRepository = new InMemorySnapshotRepository();
            Configuration.Current.UseDependency<ISnapshotRepository>(_ => snapshotRepository);

            var original = new CustomerAccount()
                .Apply(new ChangeEmailAddress(Any.Email()))
                .Apply(new RequestNoSpam());
            var repository = CreateRepository<CustomerAccount>();
            await repository.Save(original);
            await snapshotRepository.SaveSnapshot(original);

            // act
            var fromSnapshot = await repository.GetLatest(original.Id);

            // assert
            fromSnapshot.Id.Should().Be(original.Id);
            fromSnapshot.Version.Should().Be(fromSnapshot.Version);
            fromSnapshot.EmailAddress.Should().Be(fromSnapshot.EmailAddress);
            fromSnapshot.UserName.Should().Be(fromSnapshot.UserName);
            fromSnapshot.NoSpam.Should().Be(fromSnapshot.NoSpam);
        }

        [Test]
        public async Task When_new_events_are_added_to_an_aggregate_sourced_from_a_fully_current_snapshot_the_version_increments_correctly()
        {
             // arrange
            var snapshotRepository = new InMemorySnapshotRepository();
            Configuration.Current.UseDependency<ISnapshotRepository>(_ => snapshotRepository);

            var customerAccount = new CustomerAccount()
                .Apply(new ChangeEmailAddress(Any.Email()));
            var repository = CreateRepository<CustomerAccount>();
            await repository.Save(customerAccount);
            var snapshottedVersion = customerAccount.Version;

            await snapshotRepository.SaveSnapshot(customerAccount);

            // act
            var account = await repository.GetLatest(customerAccount.Id);
            account.Apply(new RequestSpam());

            // assert
            account.Version.Should().Be(snapshottedVersion+ 1);
        }

        [Test]
        public async Task When_new_events_are_added_to_an_aggregate_sourced_from_a_stale_snapshot_the_version_increments_correctly()
        {
             // arrange
            var snapshotRepository = new InMemorySnapshotRepository();
            Configuration.Current.UseDependency<ISnapshotRepository>(_ => snapshotRepository);

            var account = new CustomerAccount()
                .Apply(new ChangeEmailAddress(Any.Email()))
                .Apply(new RequestSpam());
            var originalVersion = account.Version;
            var repository = CreateRepository<CustomerAccount>();
            await repository.Save(account);
            await snapshotRepository.SaveSnapshot(account);

            await SaveEventsDirectly(new CustomerAccount.RequestedSpam
            {
                AggregateId = account.Id,
                SequenceNumber = originalVersion + 1
            }.ToInMemoryStoredEvent());

            // act
            account = await repository.GetLatest(account.Id);

            // assert
            account.Version.Should().Be(originalVersion + 1);
        }
        
        [Test]
        public async Task When_a_snapshot_is_not_up_to_date_then_GetLatest_retrieves_later_events_and_applies_them()
        {
            // arrange
            var snapshotRepository = new InMemorySnapshotRepository();
            Configuration.Current.UseDependency<ISnapshotRepository>(_ => snapshotRepository);

            var customerAccount = new CustomerAccount()
                .Apply(new ChangeEmailAddress(Any.Email()));

            var repository = CreateRepository<CustomerAccount>();

            await repository.Save(customerAccount);

            var snapshot = customerAccount.CreateSnapshot();

            Console.WriteLine(snapshot.Body);

            await snapshotRepository.SaveSnapshot(snapshot);

            await SaveEventsDirectly(new CustomerAccount.RequestedSpam
                               {
                                   AggregateId = snapshot.AggregateId,
                                   SequenceNumber = 124
                               }.ToInMemoryStoredEvent());

            // act
            var account = await repository.GetLatest(snapshot.AggregateId);

            // assert
            account.Version.Should().Be(124);
            account.NoSpam.Should().Be(false);
        }
        
        [Test]
        public async Task Scheduled_Events_that_cannot_be_deserialized_due_to_unknown_command_do_not_cause_sourcing_to_fail()
        {
            var orderId = Guid.NewGuid();
            var goodEvent = new Order.CustomerInfoChanged
            {
                CustomerName = "Waylon Jennings",
                AggregateId = orderId,
                SequenceNumber = 1
            }.ToInMemoryStoredEvent();
            var badEvent = CreateStoredEvent(
                streamName : goodEvent.StreamName,
                type : "Scheduled:UNKNOWNCOMMAND",
                aggregateId : Guid.Parse(goodEvent.AggregateId),
                sequenceNumber : 2,
                body : new
                {
                    Command = new
                    {
                        CommandName = "UNKNOWNCOMMAND"
                    }
                }.ToJson(),
                utcTime : DateTime.UtcNow);

            await SaveEventsDirectly(goodEvent, badEvent);

            var repository = CreateRepository<Order>();

            var order = await repository.GetLatest(orderId);

            order.CustomerName.Should().Be("Waylon Jennings");
        }

        [Category("Performance")]
        [Explicit]
        public async Task When_sourcing_an_aggregate_with_a_large_number_of_source_events_then_the_operation_completes_quickly()
        {
            var count = 30000;
            var aggregateId = Any.Guid();
            var largeListEvents =
                Enumerable.Range(1, count)
                          .Select(i => new EventSourcedAggregateTests.PerfTestAggregate.SimpleEvent { AggregateId = aggregateId, SequenceNumber = i });

            Console.WriteLine("{0}: Adding new events to db", DateTimeOffset.UtcNow.ToString("O"));
            await SaveEventsDirectly(largeListEvents.Select(e => e.ToInMemoryStoredEvent()).ToArray());

            var repository = CreateRepository<EventSourcedAggregateTests.PerfTestAggregate>();

            Console.WriteLine("{0}: Sourcing aggregate", DateTimeOffset.UtcNow.ToString("O"));
            var sw = Stopwatch.StartNew();
            var t = await repository.GetLatest(aggregateId);
            sw.Stop();

            Console.WriteLine("Elapsed: {0}ms", sw.ElapsedMilliseconds);

            Console.WriteLine("{0}: Removing old events from db", DateTimeOffset.UtcNow.ToString("O"));
            await DeleteEventsFromEventStore(aggregateId);

            t.Version.Should().Be(count);
            t.NumberOfUpdatesExecuted.Should().Be(count);
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }

        protected abstract InMemoryStoredEvent CreateStoredEvent(
            string streamName,
            string type,
            Guid aggregateId,
            int sequenceNumber,
            string body,
            DateTime utcTime);

        [Test]
        public async Task GetAggregate_can_be_used_within_a_consequenter_to_access_an_aggregate_without_having_to_re_source()
        {
            // arrange
            var order = new Order()
                .Apply(new ChangeCustomerInfo
                {
                    CustomerName = Any.FullName()
                })
                .Apply(new AddItem
                {
                    ProductName = "Cog",
                    Price = 9.99m
                })
                .Apply(new AddItem
                {
                    ProductName = "Sprocket",
                    Price = 9.99m
                })
                .Apply(new ProvideCreditCardInfo
                {
                    CreditCardNumber = Any.String(16, 16, Characters.Digits),
                    CreditCardCvv2 = "123",
                    CreditCardExpirationMonth = "12",
                    CreditCardName = Any.FullName(),
                    CreditCardExpirationYear = "16"
                })
                .Apply(new SpecifyShippingInfo())
                .Apply(new Place());

            var repository = CreateRepository<Order>();
            Configuration.Current.UseDependency<IEventSourcedRepository<Order>>(t =>
            {
                throw new Exception("GetAggregate should not be triggering this call.");
            });
            Order aggregate = null;
#pragma warning disable 612
#pragma warning disable 618
            var consquenter = Consequenter.Create<Order.Placed>(e => { aggregate = e.GetAggregate(); });
#pragma warning restore 618
#pragma warning restore 612
            var bus = Configuration.Current.EventBus;
            bus.Subscribe(consquenter);

            // act
            await repository.Save(order);

            // assert
            aggregate.Should().Be(order);
        }

        protected static IList<IEvent> ListenToPublishedEvents()
        {
            var publishedEvents = new List<IEvent>();
            var configuration = Configuration.Current;
            configuration.RegisterForDisposal(configuration.EventBus.Events<IEvent>().Subscribe(e => publishedEvents.Add(e)));
            return publishedEvents;
        }

        [Ignore("Scenario under consideration")]
        [Test]
        public async Task Snapshotting_can_be_bypassed()
        {
            var snapshotRepository = new InMemorySnapshotRepository();
            Configuration.Current.UseDependency<ISnapshotRepository>(_ => snapshotRepository);

            var account = new CustomerAccount()
                .Apply(new ChangeEmailAddress(Any.Email()))
                .Apply(new RequestSpam())
                .Apply(new SendMarketingEmail())
                .Apply(new SendOrderConfirmationEmail(Any.AlphanumericString(8, 8)));

            var eventSourcedRepository = CreateRepository<CustomerAccount>();
            await eventSourcedRepository.Save(account);
            await snapshotRepository.SaveSnapshot(account);

            // act
            account = await eventSourcedRepository.GetLatest(account.Id);

            // assert
            account.Events().Count()
                   .Should()
                   .Be(4);
        }
    }
}
