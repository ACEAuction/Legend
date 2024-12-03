﻿using ACE.Database;
using ACE.Entity.Models;
using ACE.Mods.Legend.Lib.Common;
using ACE.Mods.Legend.Lib.Common.Errors;
using ACE.Mods.Legend.Lib.Mail;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Shared;
using static ACE.Server.WorldObjects.Player;

namespace ACE.Mods.Legend.Lib.Auction
{
    public static class AuctionExtensions
    {
        private static readonly ushort MaxAuctionHours = 168; // a week is the longest duration for an auction, this could be a server property

        private const string AuctionPrefix = "[AuctionHouse]";

        public static void SendAuctionMessage(this Player player, string message, ChatMessageType messageType = ChatMessageType.System)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat($"{AuctionPrefix} {message}", messageType));
        }

        public static void ValidateAuctionSell(this Player player, ushort hoursDuration)
        {
            if (hoursDuration > MaxAuctionHours)
                throw new AuctionFailure($"Failed validation for auction sell, an auction end time can not exceed 168 hours (a week)");
        }

        public static void ValidateAuctionTag(this Player player, uint tagId, out WorldObject taggedItem)
        {
            if (AuctionManager.TaggedItems.TryGetValue(player.Guid.Full, out var items) && items != null && items.Contains(tagId))
                throw new AuctionFailure($"The tagged item with Id = {tagId} is already tagged");

            var item = player.FindObject(tagId, SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out var itemFoundInContainer, out var itemRootOwner, out var itemWasEquipped);

            if (item == null)
                throw new AuctionFailure($"The tagged item with Id = {tagId} was not found on your person");

            if (item.IsAttunedOrContainsAttuned)
                throw new AuctionFailure($"{item.NameWithMaterial} cannot be tagged, it is attuned or bonded");

            if (player.IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow))
                throw new AuctionFailure($"The item {item.NameWithMaterial} cannot be tagged, the item is currently being traded");

            taggedItem = item;
        }

        public static void PlaceAuctionSell(this Player player, List<uint> itemList, uint currencyType, uint startPrice, ushort hoursDuration)
        {
            List<WorldObject> removedItems = new List<WorldObject>();
            Book listingParchment = (Book)WorldObjectFactory.CreateNewWorldObject(365);

            try
            {
                player.ValidateAuctionSell(hoursDuration);

                var startTime = DateTime.UtcNow;
                var endTime = startTime.AddSeconds(hoursDuration);

                listingParchment.Name = "Auction Listing Invoice";
                listingParchment.SetProperty(FakeIID.SellerId, player.Guid.Full);
                listingParchment.SetProperty(FakeString.SellerName, player.Name);
                listingParchment.SetProperty(FakeInt.ListingCurrencyType, (int)currencyType);
                listingParchment.SetProperty(FakeInt.ListingStartPrice, (int)startPrice);
                listingParchment.SetProperty(FakeFloat.ListingStartTimestamp, (double)Time.GetUnixTime(startTime));
                listingParchment.SetProperty(FakeFloat.ListingEndTimestamp, (double)Time.GetUnixTime(endTime));
                listingParchment.SetProperty(FakeString.ListingStatus, "active");

                foreach (var item in itemList)
                {
                    player.RemoveItemForTransfer(item, out WorldObject removedItem);
                    removedItem.SetProperty(FakeIID.ListingId, listingParchment.Guid.Full);
                    removedItems.Add(removedItem);
                }

                foreach (var item in removedItems)
                {
                    if (item == null || !AuctionManager.TryAddToItemsContainer(item))
                        throw new AuctionFailure($"Failed to place auction listing, couldn't transfer listing item {item?.Name} to listing container");
                }

                if (!AuctionManager.TryAddToListingsContainer(listingParchment))
                    throw new AuctionFailure($"Failed to place auction listing, couldn't transfer listing item {listingParchment.Name} to listing container");

                var currency = "";
                Weenie weenie = DatabaseManager.World.GetCachedWeenie(currencyType);

                if (weenie == null)
                    throw new AuctionFailure($"Failed to place auction listing, currencyType does not have a valid weenie");

                currency = weenie.GetName();

                player.SendAuctionMessage($"Successfully created an auction listing with Id = {listingParchment.Guid.Full}, Seller = {player.Name}, Currency = {currency}, StartingPrice = {startPrice}", ChatMessageType.Broadcast);

                foreach (var item in removedItems)
                {
                    var message = $"--> Id = {item.Guid.Full}, {Helpers.BuildItemInfo(item)}, Count = {item.StackSize ?? 1}";
                    player.SendAuctionMessage(message);
                }

                player.ClearTags();
            }
            catch (AuctionFailure ex)
            {
                ModManager.Log(ex.Message, ModManager.LogLevel.Error);
                player.SendAuctionMessage($"placing auction listing failed");
                player.SendAuctionMessage(ex.Message);

                foreach (WorldObject removedItem in removedItems)
                {
                    removedItem.RemoveProperty(FakeIID.ListingId);

                    if (player.HasItemOnPerson(removedItem.Guid.Full, out var _))
                        continue;

                    var actionChain = new ActionChain();
                    actionChain.AddDelaySeconds(0.5);
                    actionChain.AddAction(player, () =>
                    {
                        player.SendAuctionMessage($"Attempting to return listing item {removedItem.NameWithMaterial}");

                        AuctionManager.TryRemoveFromItemsContainer(removedItem);

                        if (!player.TryCreateInInventoryWithNetworking(removedItem))
                        {
                            player.SendAuctionMessage($"Failed to return listing item {removedItem.NameWithMaterial}, attempting to send it by mail");
                            MailManager.TryAddToMailContainer(removedItem);
                        }

                        var stackSize = removedItem.StackSize ?? 1;

                        var stackMsg = stackSize != 1 ? $"{stackSize:N0} " : "";
                        var itemName = removedItem.GetNameWithMaterial(stackSize);

                        player.SendAuctionMessage($"Auction House gives you {stackMsg}{itemName}.", ChatMessageType.Broadcast);
                        player.EnqueueBroadcast(new GameMessageSound(player.Guid, Sound.ReceiveItem));
                    });
                    actionChain.EnqueueChain();
                }

                player.TryConsumeFromInventoryWithNetworking(listingParchment.Guid.Full);

                listingParchment.Destroy();
            }
        }

        public static bool HasItemOnPerson(this Player player, uint itemId, out WorldObject foundItem)
        {
            foundItem = player.FindObject(itemId, SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out var itemFoundInContainer, out var itemRootOwner, out var itemWasEquipped);
            return foundItem != null;
        }

        public static void RemoveItemForTransfer(this Player player, uint itemToTransfer, out WorldObject itemToRemove, int? amount = null)
        {
            if (player.IsBusy || player.Teleporting || player.suicideInProgress)
                throw new ItemTransferFailure($"The item cannot be transferred, you are too busy");

            var item = player.FindObject(itemToTransfer, SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out var itemFoundInContainer, out var itemRootOwner, out var itemWasEquipped);

            if (item == null)
                throw new ItemTransferFailure($"The item cannot be transferred, item with Id = {itemToTransfer} was not found on your person");

            if (item.IsAttunedOrContainsAttuned)
                throw new ItemTransferFailure($"The item cannot be transferred {item.NameWithMaterial} is attuned or bonded");

            if (player.IsTrading && item.IsBeingTradedOrContainsItemBeingTraded(player.ItemsInTradeWindow))
                throw new ItemTransferFailure($"The item cannot be transferred {item.NameWithMaterial}, the item is currently being traded");

            var removeAmount = amount.HasValue ? amount.Value : item.StackSize ?? 1;

            if (!player.RemoveItemForGive(item, itemFoundInContainer, itemWasEquipped, itemRootOwner, removeAmount, out WorldObject itemToGive))
                throw new ItemTransferFailure($"The item cannot be transferred {item.NameWithMaterial}, failed to remove item from location");

            itemToRemove = itemToGive;
        }

        public static void InspectTagItem(this Player player, uint itemId)
        {
            player.ValidateAuctionTag(itemId, out WorldObject item);
            player.SendAuctionMessage($"Auction Tag Information, Id = {item.Guid.Full}, {Helpers.BuildItemInfo(item)}");
            player.AddTagItem(itemId);
        }

        public static void AddTagItem(this Player player, uint itemId)
        {
            player.ValidateAuctionTag(itemId, out WorldObject item);

            AuctionManager.TaggedItems.AddOrUpdate(
                player.Guid.Full,
                _ => new HashSet<uint> { itemId },
                (_, existingSet) =>
                {
                    lock (existingSet)
                    {
                        existingSet.Add(itemId);
                    }
                    return existingSet;
                }
            );

            player.SendAuctionMessage($"Added tagged listing item {item.NameWithMaterial}");
        }

        public static void ListTags(this Player player)
        {
            if (AuctionManager.TaggedItems.TryGetValue(player.Guid.Full, out var items) && items.Count > 0)
            {
                StringBuilder message = new StringBuilder();

                lock (items)
                {
                    message.Append("^^^^^^^^^^^^^^^^^^^^^^^^^\n");
                    message.Append("Auction Sell Tagged List\n");
                    message.Append("-------------------------\n");
                    foreach (var id in items)
                    {
                        var item = player.FindObject(id, SearchLocations.MyInventory | SearchLocations.MyEquippedItems, out var itemFoundInContainer, out var itemRootOwner, out var itemWasEquipped);

                        if (item == null)
                            message.Append($"--> Id = {id}  Unable to find item\n");
                        else
                            message.Append($"--> Id = {item.Guid.Full}, {Helpers.BuildItemInfo(item)}\n");

                        message.Append("-------------------------\n");
                    }
                    message.Append("^^^^^^^^^^^^^^^^^^^^^^^^^\n");
                }

                CommandHandlerHelper.WriteOutputInfo(player.Session, message.ToString(), ChatMessageType.Broadcast);
            }
            else
                player.Session.Network.EnqueueSend(new GameMessageSystemChat("You don't have any tagged items", ChatMessageType.System));
        }

        public static void RemoveTagItem(this Player player, uint itemId)
        {
            lock (AuctionManager.TaggedItems)
            {
                if (AuctionManager.TaggedItems.TryGetValue(player.Guid.Full, out var items))
                {
                    if (items.Remove(itemId))
                    {
                        player.SendAuctionMessage($"You have removed item with Id = {itemId} from your tagged list");
                    }
                    else
                    {
                        player.SendAuctionMessage($"Item with Id = {itemId} was not found in your tagged list");
                    }
                }
                else
                {
                    player.SendAuctionMessage($"You can't remove item with Id = {itemId}, you don't have a tagged list");
                }
            }
        }

        public static void ClearTags(this Player player)
        {
            lock (AuctionManager.TaggedItems)
            {
                if (AuctionManager.TaggedItems.TryGetValue(player.Guid.Full, out var items))
                {
                    items.Clear();
                }

                player.SendAuctionMessage($"You have cleared your tagged list");
            }
        }

    }
}