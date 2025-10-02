using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Linq;
using static ConversationData;
using static NPCTalking;

namespace Oxide.Plugins
{
    [Info("Vehicle Vendor Options", "WhiteThunder", "1.7.8")]
    [Description("Allows customizing vehicle fuel and prices at NPC vendors.")]
    internal class VehicleVendorOptions : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private readonly Plugin Economics, ServerRewards;

        private Configuration _config;

        private const string Permission_Allow_All = "vehiclevendoroptions.allow.all";
        private const string Permission_Free_All = "vehiclevendoroptions.free.allvehicles";
        private const string Permission_Ownership_All = "vehiclevendoroptions.ownership.allvehicles";

        private const string Permission_Price_Prefix = "vehiclevendoroptions.price";

        private const int MinHiddenSlot = 24;
        private const int ScrapItemId = -932201673;
        private const float VanillaDespawnProtectionTime = 300;

        private readonly object False = false;

        private Item _scrapItem;
        private readonly VehicleInfoManager _vehicleInfoManager;

        public VehicleVendorOptions()
        {
            _vehicleInfoManager = new VehicleInfoManager(this);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(Permission_Allow_All, this);
            permission.RegisterPermission(Permission_Free_All, this);
            permission.RegisterPermission(Permission_Ownership_All, this);

            _vehicleInfoManager.Init();
        }

        private void OnServerInitialized()
        {
            _scrapItem = ItemManager.CreateByItemID(ScrapItemId);
            _vehicleInfoManager.OnServerInitialized();
        }

        private void Unload()
        {
            CostLabelUI.DestroyAll();
            _scrapItem?.Remove();
        }

        private void OnEntitySpawned(HotAirBalloon vehicle) => HandleSpawn(vehicle);

        private void OnEntitySpawned(PlayerHelicopter vehicle) => HandleSpawn(vehicle);

        private void OnEntitySpawned(MotorRowboat vehicle) => HandleSpawn(vehicle);

        private void OnEntitySpawned(BaseSubmarine vehicle) => HandleSpawn(vehicle);

        private object OnRidableAnimalClaim(RidableHorse horse, BasePlayer player, Item saddleItem)
        {
            if (!horse.IsForSale)
                return null;

            var vehicleInfo = _vehicleInfoManager.GetVehicleInfo(horse);
            if (vehicleInfo == null)
                return null;

            if (!HasPermission(player.UserIDString, Permission_Free_All)
                && !HasPermission(player.UserIDString, vehicleInfo.FreePermission))
                return null;

            horse.SetFlag(BaseEntity.Flags.Reserved2, false);
            if (saddleItem != null)
            {
                horse.OnClaimedWithToken(saddleItem);
            }
            else
            {
                // If the saddle item is null, that means the hook is out of date, so just use one seat for now.
                horse.SetFlag(BaseEntity.Flags.Reserved9, true, networkupdate: false);
                horse.SetFlag(BaseEntity.Flags.Reserved10, false);
                horse.UpdateMountFlags();
            }

            horse.AttemptMount(player, doMountChecks: false);
            Interface.CallHook("OnRidableAnimalClaimed", horse, player);
            return False;
        }

        private void OnRidableAnimalClaimed(RidableHorse horse, BasePlayer player)
        {
            SetOwnerIfPermission(horse, player);
        }

        private object OnNpcConversationRespond(VehicleVendor vendor, BasePlayer player, ConversationData conversationData, ResponseNode responseNode)
        {
            CostLabelUI.Destroy(player);

            var resultingSpeechNode = ConversationUtils.FindSpeechNodeByName(conversationData, responseNode.resultingSpeechNode);
            if (resultingSpeechNode == null)
                return null;

            var vehicleInfo = _vehicleInfoManager.GetForPayPrompt(resultingSpeechNode.shortname);
            if (vehicleInfo != null)
            {
                // Player has selected a specific vehicle.
                return HandlePayPrompt(vendor, player, resultingSpeechNode, vehicleInfo);
            }

            if (!string.IsNullOrEmpty(responseNode.actionString))
            {
                vehicleInfo = _vehicleInfoManager.GetForPayAction(responseNode.actionString);
                if (vehicleInfo == null)
                    return null;

                // Player has selected the option to pay for the vehicle.
                return HandlePayment(vendor, player, conversationData, responseNode, vehicleInfo);
            }

            return null;
        }

        private void OnNpcConversationEnded(VehicleVendor vendor, BasePlayer player)
        {
            CostLabelUI.Destroy(player);
        }

        #endregion

        #region Helpers

        private static void AdjustFuel(VehicleSpawner.IVehicleSpawnUser vehicle, int desiredFuelAmount)
        {
            if (vehicle.GetFuelSystem() is not EntityFuelSystem fuelSystem)
                return;

            var fuelAmount = desiredFuelAmount < 0
                ? fuelSystem.GetFuelContainer().allowedItem.stackable
                : desiredFuelAmount;

            var fuelItem = fuelSystem.GetFuelItem();
            if (fuelItem != null && fuelItem.amount != fuelAmount)
            {
                fuelItem.amount = fuelAmount;
                fuelItem.MarkDirty();
            }
        }

        private void HandleSpawn(VehicleSpawner.IVehicleSpawnUser vehicle)
        {
            if (Rust.Application.isLoadingSave)
                return;

            var vehicle2 = vehicle;
            NextTick(() =>
            {
                var entity = vehicle2 as BaseEntity;
                if (entity?.creatorEntity == null)
                    return;

                var vehicleConfig = _vehicleInfoManager.GetVehicleInfo(entity)?.VehicleConfig;
                if (vehicleConfig == null)
                    return;

                AdjustFuel(vehicle2, vehicleConfig.FuelAmount);
                MaybeSetOwner(entity);

                var spawnTimeDelta = vehicleConfig.DespawnProtectionSeconds - VanillaDespawnProtectionTime;

                var baseVehicle = entity as BaseVehicle;
                if ((object)baseVehicle != null)
                {
                    baseVehicle.spawnTime += spawnTimeDelta;
                }

                var hotAirBalloon = entity as HotAirBalloon;
                if ((object)hotAirBalloon != null)
                {
                    hotAirBalloon.spawnTime += spawnTimeDelta;
                }
            });
        }

        private void MaybeSetOwner(BaseEntity vehicle)
        {
            var basePlayer = vehicle.creatorEntity as BasePlayer;
            if (basePlayer == null)
                return;

            SetOwnerIfPermission(vehicle, basePlayer);
        }

        private void SetOwnerIfPermission(BaseEntity vehicle, BasePlayer basePlayer)
        {
            var vehicleInfo = _vehicleInfoManager.GetVehicleInfo(vehicle);
            if (vehicle == null)
                return;

            if (HasPermission(basePlayer.UserIDString, Permission_Ownership_All)
                || HasPermission( basePlayer.UserIDString, vehicleInfo.OwnershipPermission))
            {
                vehicle.OwnerID = basePlayer.userID;
            }
        }

        private bool HasPermission(string userIdString, string perm)
        {
            return permission.UserHasPermission(userIdString, perm);
        }

        private object HandlePayPrompt(VehicleVendor vendor, BasePlayer player, SpeechNode resultingSpeechNode, VehicleInfo vehicleInfo)
        {
            if (vehicleInfo.VehicleConfig.RequiresPermission
                && !HasPermission(player.UserIDString, Permission_Allow_All)
                && !HasPermission(player.UserIDString, vehicleInfo.PurchasePermission))
            {
                // End the conversation instead of showing the option to pay.
                ConversationUtils.ForceSpeechNode(vendor, player, ConversationUtils.SpeechNodes.Goodbye);
                ChatMessage(player, "Error.Vehicle.NoPermission");
                return False;
            }

            // Player has permission, so check if we need to send a UI and fake inventory snapshot.
            var scrapCondition = ConversationUtils.FindPayConditionInResponses(resultingSpeechNode);
            if (scrapCondition != null)
            {
                var vanillaPrice = (int)scrapCondition.conditionAmount;

                var priceConfig = vehicleInfo.VehicleConfig.GetPriceForPlayer(this, player.IPlayer, vehicleInfo.FreePermission);
                if (priceConfig == null || priceConfig.MatchesVanillaPrice(vanillaPrice))
                    return null;

                // Always send the UI if the price is custom, regardless of whether the player has enough.
                CostLabelUI.Create(this, player, priceConfig);

                var playerScrapAmount = player.inventory.GetAmount(ScrapItemId);
                var canAffordVanillaPrice = playerScrapAmount >= vanillaPrice;
                var canAffordCustomPrice = priceConfig.CanPlayerAfford(player);

                if (canAffordCustomPrice == canAffordVanillaPrice)
                    return null;

                // Either the client has enough but thinks it doesn't, or doesn't have enough but thinks it does.
                // Add or remove scrap so the vanilla logic for showing the payment option will match the custom payment logic.
                var addOrRemoveScrapAmount = canAffordCustomPrice ? vanillaPrice : -playerScrapAmount;
                PlayerInventoryUtils.UpdateWithFakeScrap(player, _scrapItem, addOrRemoveScrapAmount);

                var player2 = player;
                // Refresh the player inventory to clear out the fake snapshot, after a few seconds.
                // This delay needs to be long enough for the text to print out, which could vary by language.
                player2.Invoke(() => PlayerInventoryUtils.Refresh(player2), 3f);
            }

            return null;
        }

        // This method is mostly vanilla logic, with some changes to modify the price.
        private object HandlePayment(VehicleVendor vendor, BasePlayer player, ConversationData conversationData, ResponseNode responseNode, VehicleInfo vehicleInfo)
        {
            if (responseNode.conditions.Length != 0)
            {
                vendor.UpdateFlags();
            }

            var scrapCondition = ConversationUtils.GetScrapCondition(responseNode);
            if (scrapCondition == null)
                return null;

            var resultAction = ConversationUtils.FindResultAction(vendor, vehicleInfo.PayAction);
            if (resultAction == null)
                return null;

            var vanillaPrice = (int)scrapCondition.conditionAmount;

            var priceConfig = vehicleInfo.VehicleConfig.GetPriceForPlayer(this, player.IPlayer, vehicleInfo.FreePermission);
            if (priceConfig == null || priceConfig.MatchesVanillaPrice(vanillaPrice))
                return null;

            if (!priceConfig.CanPlayerAfford(player))
            {
                ConversationUtils.ForceSpeechNode(vendor, player, ConversationUtils.SpeechNodes.Goodbye);
                return False;
            }

            if (priceConfig.RequiresScrap)
            {
                // Set the scrap price to the custom amount.
                scrapCondition.conditionAmount = (uint)priceConfig.Amount;
                resultAction.scrapCost = priceConfig.Amount;
            }
            else
            {
                // Set the scrap price to 0 since the player is being charged for custom currency.
                priceConfig.TryChargePlayer(player);
                scrapCondition.conditionAmount = 0;
                resultAction.scrapCost = 0;
            }

            bool passesConditions;

            try
            {
                passesConditions = responseNode.PassesConditions(player, vendor);
                if (passesConditions && !string.IsNullOrEmpty(responseNode.actionString))
                {
                    vendor.OnConversationAction(player, responseNode.actionString);
                }
            }
            finally
            {
                // Revert scrap price to vanilla.
                scrapCondition.conditionAmount = (uint)vanillaPrice;
                resultAction.scrapCost = vanillaPrice;
            }

            var speechNodeIndex = conversationData.GetSpeechNodeIndex(passesConditions
                ? responseNode.resultingSpeechNode
                : responseNode.GetFailedSpeechNode(player, vendor));

            if (speechNodeIndex == -1)
            {
                vendor.ForceEndConversation(player);
                return False;
            }

            vendor.ForceSpeechNode(player, speechNodeIndex);
            Interface.CallHook("OnNpcConversationResponded", this, player, conversationData, responseNode);

            return False;
        }

        #endregion

        #region Vehicle Info

        private class VehicleInfo
        {
            public string PrefabPath;
            public string PermissionSuffix;
            public VehicleConfig VehicleConfig;
            public string PayPrompt;
            public string PayAction;

            public uint PrefabId { get; private set; }
            public string PurchasePermission { get; private set; }
            public string OwnershipPermission { get; private set; }
            public string FreePermission { get; private set; }

            public void Init()
            {
                PurchasePermission = $"{nameof(VehicleVendorOptions)}.allow.{PermissionSuffix}".ToLower();
                OwnershipPermission = $"{nameof(VehicleVendorOptions)}.ownership.{PermissionSuffix}".ToLower();
                FreePermission = $"{nameof(VehicleVendorOptions)}.free.{PermissionSuffix}".ToLower();
            }

            public void OnServerInitialized()
            {
                var entity = GameManager.server.FindPrefab(PrefabPath)?.GetComponent<BaseEntity>();
                if (entity != null)
                {
                    PrefabId = entity.prefabID;
                }
            }
        }

        private class VehicleInfoManager
        {
            private readonly VehicleVendorOptions _plugin;
            private readonly Dictionary<uint, VehicleInfo> _prefabIdToVehicleInfo = new();
            private readonly Dictionary<string, VehicleInfo> _payPromptToVehicleInfo = new();
            private readonly Dictionary<string, VehicleInfo> _payActionToVehicleInfo = new();
            private VehicleInfo[] _allVehicles;

            public VehicleInfoManager(VehicleVendorOptions plugin)
            {
                _plugin = plugin;
            }

            public void Init()
            {
                _allVehicles = new[]
                {
                    new VehicleInfo
                    {
                        PrefabPath = "assets/content/vehicles/minicopter/minicopter.entity.prefab",
                        PermissionSuffix = "minicopter",
                        VehicleConfig = _plugin._config.Vehicles.Minicopter,
                        PayPrompt = "minicopterbuy",
                        PayAction = "buyminicopter",
                    },
                    new VehicleInfo
                    {
                        PrefabPath = "assets/content/vehicles/scrap heli carrier/scraptransporthelicopter.prefab",
                        PermissionSuffix = "scraptransport",
                        VehicleConfig = _plugin._config.Vehicles.ScrapTransport,
                        PayPrompt = "transportbuy",
                        PayAction = "buytransport",
                    },
                    new VehicleInfo
                    {
                        PrefabPath = "assets/content/vehicles/attackhelicopter/attackhelicopter.entity.prefab",
                        PermissionSuffix = "attackhelicopter",
                        VehicleConfig = _plugin._config.Vehicles.AttackHelicopter,
                        PayPrompt = "attackbuy",
                        PayAction = "buyattack",
                    },
                    new VehicleInfo
                    {
                        PrefabPath = "assets/prefabs/deployable/hot air balloon/hotairballoon.prefab",
                        PermissionSuffix = "hotairballoon",
                        VehicleConfig = _plugin._config.Vehicles.HotAirBalloon,
                        PayPrompt = "habbuy",
                        PayAction = "buyhab",
                    },

                    new VehicleInfo
                    {
                        PrefabPath = "assets/content/vehicles/boats/rowboat/rowboat.prefab",
                        PermissionSuffix = "rowboat",
                        VehicleConfig = _plugin._config.Vehicles.Rowboat,
                        PayPrompt = "pay_rowboat",
                        PayAction = "buyboat",
                    },
                    new VehicleInfo
                    {
                        PrefabPath = "assets/content/vehicles/boats/rhib/rhib.prefab",
                        PermissionSuffix = "rhib",
                        VehicleConfig = _plugin._config.Vehicles.RHIB,
                        PayPrompt = "pay_rhib",
                        PayAction = "buyrhib",
                    },
                    new VehicleInfo
                    {
                        PrefabPath = "assets/content/vehicles/submarine/submarinesolo.entity.prefab",
                        PermissionSuffix = "solosub",
                        VehicleConfig = _plugin._config.Vehicles.SoloSub,
                        PayPrompt = "pay_sub",
                        PayAction = "buysub",
                    },
                    new VehicleInfo
                    {
                        PrefabPath = "assets/content/vehicles/submarine/submarineduo.entity.prefab",
                        PermissionSuffix = "duosub",
                        VehicleConfig = _plugin._config.Vehicles.DuoSub,
                        PayPrompt = "pay_duosub",
                        PayAction = "buysubduo",
                    },

                    new VehicleInfo
                    {
                        PrefabPath = "assets/content/vehicles/horse/ridablehorse.prefab",
                        PermissionSuffix = "ridablehorse",
                    },
                };

                foreach (var vehicleInfo in _allVehicles)
                {
                    vehicleInfo.Init();

                    if (!string.IsNullOrEmpty(vehicleInfo.PayPrompt))
                    {
                        _payPromptToVehicleInfo[vehicleInfo.PayPrompt] = vehicleInfo;
                    }

                    if (!string.IsNullOrEmpty(vehicleInfo.PayAction))
                    {
                        _payActionToVehicleInfo[vehicleInfo.PayAction] = vehicleInfo;
                    }
                }

                // Register permissions in batches by permission type.
                foreach (var vehicleInfo in _allVehicles)
                {
                    _plugin.permission.RegisterPermission(vehicleInfo.PurchasePermission, _plugin);
                }

                foreach (var vehicleInfo in _allVehicles)
                {
                    _plugin.permission.RegisterPermission(vehicleInfo.OwnershipPermission, _plugin);
                }

                foreach (var vehicleInfo in _allVehicles)
                {
                    _plugin.permission.RegisterPermission(vehicleInfo.FreePermission, _plugin);
                }

                foreach (var vehicleInfo in _allVehicles)
                {
                    vehicleInfo.VehicleConfig?.InitAndValidate(_plugin, vehicleInfo.PermissionSuffix);
                }
            }

            public void OnServerInitialized()
            {
                foreach (var vehicleInfo in _allVehicles)
                {
                    vehicleInfo.OnServerInitialized();

                    if (vehicleInfo.PrefabId != 0)
                    {
                        _prefabIdToVehicleInfo[vehicleInfo.PrefabId] = vehicleInfo;
                    }
                    else
                    {
                        _plugin.LogError($"Unable to determine Prefab ID for prefab: {vehicleInfo.PrefabPath}");
                    }
                }
            }

            public VehicleInfo GetVehicleInfo(BaseEntity entity)
            {
                return _prefabIdToVehicleInfo.GetValueOrDefault(entity.prefabID);
            }

            public VehicleInfo GetForPayPrompt(string promptName)
            {
                return _payPromptToVehicleInfo.GetValueOrDefault(promptName);
            }

            public VehicleInfo GetForPayAction(string actionName)
            {
                return _payActionToVehicleInfo.GetValueOrDefault(actionName);
            }
        }

        #endregion

        #region Player Inventory Utilities

        private static class PlayerInventoryUtils
        {
            public static void Refresh(BasePlayer player)
            {
                player.inventory.SendUpdatedInventory(PlayerInventory.Type.Main, player.inventory.containerMain);
            }

            public static void UpdateWithFakeScrap(BasePlayer player, Item scrapItem, int amountDiff)
            {
                using var containerUpdate = Facepunch.Pool.Get<ProtoBuf.UpdateItemContainer>();
                containerUpdate.type = (int)PlayerInventory.Type.Main;
                containerUpdate.container = Facepunch.Pool.Get<List<ProtoBuf.ItemContainer>>();

                var containerInfo = player.inventory.containerMain.Save();
                var itemSlot = AddFakeScrapToContainerUpdate(containerInfo, scrapItem, amountDiff);

                containerUpdate.container.Capacity = itemSlot + 1;
                containerUpdate.container.Add(containerInfo);
                player.ClientRPCPlayer(null, player, "UpdatedItemContainer", containerUpdate);
            }

            private static int AddFakeScrapToContainerUpdate(ProtoBuf.ItemContainer containerInfo, Item scrapItem, int scrapAmount)
            {
                // Always use a separate item so it can be placed out of view.
                var itemInfo = scrapItem.Save();
                itemInfo.amount = scrapAmount;
                itemInfo.slot = GetNextAvailableSlot(containerInfo);
                containerInfo.contents.Add(itemInfo);
                return itemInfo.slot;
            }

            private static int GetNextAvailableSlot(ProtoBuf.ItemContainer containerInfo)
            {
                var highestSlot = MinHiddenSlot;
                foreach (var item in containerInfo.contents)
                {
                    if (item.slot > highestSlot)
                    {
                        highestSlot = item.slot;
                    }
                }

                return highestSlot;
            }
        }

        #endregion

        #region Conversation Utilities

        private static class ConversationUtils
        {
            public static class SpeechNodes
            {
                public const string Goodbye = "goodbye";
            }

            public static ConversationCondition GetScrapCondition(ResponseNode responseNode)
            {
                foreach (var condition in responseNode.conditions)
                {
                    if (condition.conditionType == ConversationCondition.ConditionType.HasScrap)
                        return condition;
                }

                return null;
            }

            public static ConversationCondition FindPayConditionInResponses(SpeechNode speechNode)
            {
                foreach (var futureResponseOption in speechNode.responses)
                {
                    var scrapCondition = GetScrapCondition(futureResponseOption);
                    if (scrapCondition != null)
                    {
                        return scrapCondition;
                    }
                }

                return null;
            }

            public static void ForceSpeechNode(NPCTalking npcTalking, BasePlayer player, string speechNodeName)
            {
                var speechNodeIndex = npcTalking.GetConversationFor(player).GetSpeechNodeIndex(speechNodeName);
                npcTalking.ForceSpeechNode(player, speechNodeIndex);
            }

            public static NPCConversationResultAction FindResultAction(NPCTalking npcTalking, string actionString)
            {
                if (string.IsNullOrEmpty(actionString))
                    return null;

                foreach (var resultAction in npcTalking.conversationResultActions)
                {
                    if (resultAction.action == actionString)
                        return resultAction;
                }

                return null;
            }

            public static SpeechNode FindSpeechNodeByName(ConversationData conversationData, string speechNodeName)
            {
                foreach (var speechNode in conversationData.speeches)
                {
                    if (speechNode.shortname == speechNodeName)
                        return speechNode;
                }

                return null;
            }
        }

        #endregion

        #region UI

        private static class CostLabelUI
        {
            private const string Name = "VehicleVendorOptions";

            public static void Destroy(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, Name);
            }

            public static void DestroyAll()
            {
                foreach (var player in BasePlayer.activePlayerList)
                {
                    Destroy(player);
                }
            }

            public static void Create(VehicleVendorOptions plugin, BasePlayer player, PriceConfig priceConfig)
            {
                var itemPrice = priceConfig.Amount == 0
                    ? plugin.GetMessage(player, "UI.Price.Free")
                    : priceConfig.PaymentProvider is EconomicsPaymentProvider
                    ? plugin.GetMessage(player, "UI.Currency.Economics", priceConfig.Amount)
                    : priceConfig.PaymentProvider is ServerRewardsPaymentProvider
                    ? plugin.GetMessage(player, "UI.Currency.ServerRewards", priceConfig.Amount)
                    : $"{priceConfig.Amount} {plugin.GetMessage(player, plugin.GetItemNameLocalizationKey(priceConfig.ItemShortName))}";

                var cuiElements = new CuiElementContainer
                {
                    {
                        new CuiLabel
                        {
                            RectTransform =
                            {
                                AnchorMin = "0.5 0.5",
                                AnchorMax = "0.5 0.5",
                                OffsetMin = "152 21",
                                OffsetMax = "428 41",
                            },
                            Text =
                            {
                                Text = plugin.GetMessage(player, "UI.ActualPrice", itemPrice),
                                FontSize = 11,
                                Font = "robotocondensed-regular.ttf",
                                Align = UnityEngine.TextAnchor.MiddleLeft,
                            },
                        },
                        "Overlay",
                        Name
                    },
                };

                CuiHelper.AddUi(player, cuiElements);
            }
        }

        #endregion

        #region Payment Providers

        private interface IPaymentProvider
        {
            bool IsAvailable { get; }
            int GetBalance(BasePlayer player);
            void TakeBalance(BasePlayer player, int amount);
        }

        private class EconomicsPaymentProvider : IPaymentProvider
        {
            private readonly VehicleVendorOptions _plugin;
            private Plugin _ownerPlugin => _plugin.Economics;

            public EconomicsPaymentProvider(VehicleVendorOptions plugin)
            {
                _plugin = plugin;
            }

            public bool IsAvailable => _ownerPlugin != null;

            public int GetBalance(BasePlayer player)
            {
                return Convert.ToInt32(_ownerPlugin.Call("Balance", (ulong)player.userID));
            }

            public void TakeBalance(BasePlayer player, int amount)
            {
                _ownerPlugin.Call("Withdraw", (ulong)player.userID, Convert.ToDouble(amount));
            }
        }

        private class ServerRewardsPaymentProvider : IPaymentProvider
        {
            private readonly VehicleVendorOptions _plugin;
            private Plugin _ownerPlugin => _plugin.ServerRewards;

            public ServerRewardsPaymentProvider(VehicleVendorOptions plugin)
            {
                _plugin = plugin;
            }

            public bool IsAvailable => _ownerPlugin != null;

            public int GetBalance(BasePlayer player)
            {
                return Convert.ToInt32(_ownerPlugin.Call("CheckPoints", (ulong)player.userID));
            }

            public void TakeBalance(BasePlayer player, int amount)
            {
                _ownerPlugin.Call("TakePoints", (ulong)player.userID, amount);
            }
        }

        private class ItemsPaymentProvider : IPaymentProvider
        {
            public bool IsAvailable => true;

            private int _itemId;

            public ItemsPaymentProvider(int itemId)
            {
                _itemId = itemId;
            }

            public int GetBalance(BasePlayer player)
            {
                return player.inventory.GetAmount(_itemId);
            }

            public void TakeBalance(BasePlayer player, int amount)
            {
                player.inventory.Take(null, _itemId, amount);
            }
        }

        #endregion

        #region Configuration

        private class VehicleConfigMap
        {
            [JsonProperty("Minicopter")]
            public VehicleConfig Minicopter = new()
            {
                FuelAmount = 100,
                PricesRequiringPermission = new[]
                {
                    new PriceConfig { Amount = 500 },
                    new PriceConfig { Amount = 250 },
                },
            };

            [JsonProperty("ScrapTransport")]
            public VehicleConfig ScrapTransport = new()
            {
                FuelAmount = 100,
                PricesRequiringPermission = new[]
                {
                    new PriceConfig { Amount = 800 },
                    new PriceConfig { Amount = 400 },
                },
            };

            [JsonProperty("AttackHelicopter")]
            public VehicleConfig AttackHelicopter = new()
            {
                FuelAmount = 100,
                PricesRequiringPermission = new[]
                {
                    new PriceConfig { Amount = 1750 },
                    new PriceConfig { Amount = 1250 },
                },
            };

            [JsonProperty("HotAirBalloon")]
            public VehicleConfig HotAirBalloon = new()
            {
                FuelAmount = 75,
                PricesRequiringPermission = new[]
                {
                    new PriceConfig { Amount = 100 },
                    new PriceConfig { Amount = 50 },
                },
            };

            [JsonProperty("Rowboat")]
            public VehicleConfig Rowboat = new()
            {
                FuelAmount = 50,
                PricesRequiringPermission = new[]
                {
                    new PriceConfig { Amount = 80 },
                    new PriceConfig { Amount = 40 },
                },
            };

            [JsonProperty("RHIB")]
            public VehicleConfig RHIB = new()
            {
                FuelAmount = 50,
                PricesRequiringPermission = new[]
                {
                    new PriceConfig { Amount = 200 },
                    new PriceConfig { Amount = 100 },
                },
            };

            [JsonProperty("SoloSub")]
            public VehicleConfig SoloSub = new()
            {
                FuelAmount = 50,
                PricesRequiringPermission = new[]
                {
                    new PriceConfig { Amount = 125 },
                    new PriceConfig { Amount = 50 },
                },
            };

            [JsonProperty("DuoSub")]
            public VehicleConfig DuoSub = new()
            {
                FuelAmount = 50,
                PricesRequiringPermission = new[]
                {
                    new PriceConfig { Amount = 200 },
                    new PriceConfig { Amount = 100 },
                },
            };
        }

        private class VehicleConfig
        {
            private static PriceConfig FreePriceConfig = new() { Amount = 0 };

            [JsonProperty("RequiresPermission")]
            public bool RequiresPermission = false;

            [JsonProperty("FuelAmount")]
            public int FuelAmount = 100;

            [JsonProperty("DespawnProtectionSeconds")]
            public float DespawnProtectionSeconds = 300;

            [JsonProperty("PricesRequiringPermission")]
            public PriceConfig[] PricesRequiringPermission = Array.Empty<PriceConfig>();

            public void InitAndValidate(VehicleVendorOptions plugin, string vehicleType)
            {
                foreach (var priceConfig in PricesRequiringPermission)
                {
                    priceConfig.InitAndValidate(plugin, vehicleType);
                    plugin.permission.RegisterPermission(priceConfig.Permission, plugin);
                }
            }

            public PriceConfig GetPriceForPlayer(VehicleVendorOptions plugin, IPlayer player, string freePermission)
            {
                if (plugin.HasPermission(player.Id, Permission_Free_All)
                    || plugin.HasPermission(player.Id, freePermission))
                    return FreePriceConfig;

                if (PricesRequiringPermission == null)
                    return null;

                for (var i = PricesRequiringPermission.Length - 1; i >= 0; i--)
                {
                    var priceConfig = PricesRequiringPermission[i];
                    if (priceConfig.IsValid && player.HasPermission(priceConfig.Permission))
                        return priceConfig;
                }

                return null;
            }
        }

        private class PriceConfig
        {
            [JsonProperty("Amount")]
            public int Amount;

            [JsonProperty("ItemShortName", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string ItemShortName = "scrap";

            [JsonProperty("UseEconomics")]
            public bool UseEconomics = false;

            [JsonProperty("UseServerRewards")]
            public bool UseServerRewards = false;

            [JsonIgnore]
            public string Permission;

            [JsonIgnore]
            public IPaymentProvider PaymentProvider;

            [JsonIgnore]
            public bool IsValid => (PaymentProvider?.IsAvailable ?? false) && Permission != string.Empty;

            [JsonIgnore]
            public bool RequiresScrap => PaymentProvider is ItemsPaymentProvider
                                         && ItemShortName == "scrap";

            private ItemDefinition _itemDefinition;
            [JsonIgnore]
            public ItemDefinition ItemDef
            {
                get
                {
                    if (_itemDefinition == null)
                    {
                        _itemDefinition = ItemManager.FindItemDefinition(ItemShortName);
                    }

                    return _itemDefinition;
                }
            }

            public bool MatchesVanillaPrice(int vanillaPrice)
            {
                return RequiresScrap && Amount == vanillaPrice;
            }

            public void InitAndValidate(VehicleVendorOptions plugin, string vehicleType)
            {
                Permission = GeneratePermission(vehicleType);
                PaymentProvider = CreatePaymentProvider(plugin);
            }

            private IPaymentProvider CreatePaymentProvider(VehicleVendorOptions plugin)
            {
                if (UseEconomics)
                    return new EconomicsPaymentProvider(plugin);

                if (UseServerRewards)
                    return new ServerRewardsPaymentProvider(plugin);

                if (ItemDef == null)
                {
                    plugin.LogError($"Price config contains an invalid item short name: '{ItemShortName}'.");
                    return null;
                }

                return new ItemsPaymentProvider(ItemDef.itemid);
            }

            private string GeneratePermission(string vehicleType)
            {
                if (Amount == 0)
                {
                    Permission = $"{Permission_Price_Prefix}.{vehicleType}.free";
                }
                else
                {
                    var currencyType = UseEconomics ? "economics"
                        : UseServerRewards ? "serverrewards"
                        : ItemShortName;

                    if (string.IsNullOrEmpty(ItemShortName))
                        return string.Empty;

                    Permission = $"{Permission_Price_Prefix}.{vehicleType}.{currencyType}.{Amount}";
                }

                return Permission;
            }

            public bool CanPlayerAfford(BasePlayer player)
            {
                if (Amount <= 0)
                    return true;

                return PaymentProvider.GetBalance(player) >= Amount;
            }

            public bool TryChargePlayer(BasePlayer player)
            {
                if (Amount <= 0)
                    return true;

                PaymentProvider.TakeBalance(player, Amount);
                return true;
            }
        }

        private class Configuration : BaseConfiguration
        {
            [JsonProperty("Vehicles")]
            public VehicleConfigMap Vehicles = new();
        }

        private Configuration GetDefaultConfig() => new();

        #endregion

        #region Configuration Helpers

        private class BaseConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(BaseConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            var changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                if (currentRaw.TryGetValue(key, out var currentRawValue))
                {
                    var currentDictValue = currentRawValue as Dictionary<string, object>;
                    if (currentWithDefaults[key] is Dictionary<string, object> defaultDictValue)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                        {
                            changed = true;
                        }
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_config))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #region Localization

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.UserIDString, messageName), args));

        private string GetMessage(BasePlayer player, string messageName, params object[] args) =>
            GetMessage(player.IPlayer, messageName, args);

        private string GetMessage(IPlayer player, string messageName, params object[] args) =>
            GetMessage(player.Id, messageName, args);

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private string GetItemNameLocalizationKey(string itemShortName) => $"Item.{itemShortName}";

        private void AddEnglishItemNamesForPriceConfigs(Dictionary<string, string> messages, PriceConfig[] priceConfigs)
        {
            foreach (var priceConfig in priceConfigs)
            {
                if (string.IsNullOrEmpty(priceConfig.ItemShortName))
                    continue;

                var localizationKey = GetItemNameLocalizationKey(priceConfig.ItemShortName);
                messages[localizationKey] = priceConfig.ItemDef.displayName.english;
            }
        }

        protected override void LoadDefaultMessages()
        {
            var messages = new Dictionary<string, string>
            {
                ["Error.Vehicle.NoPermission"] = "You don't have permission to buy that vehicle.",
                ["UI.ActualPrice"] = "Actual price: {0}",
                ["UI.Price.Free"] = "Free",
                ["UI.Currency.Economics"] = "{0:C}",
                ["UI.Currency.ServerRewards"] = "{0} reward points",
            };

            if (Translate.englishBaseStrings == null)
            {
                Translate.CacheEnglishStrings();
            }

            AddEnglishItemNamesForPriceConfigs(messages, _config.Vehicles.Minicopter.PricesRequiringPermission);
            AddEnglishItemNamesForPriceConfigs(messages, _config.Vehicles.ScrapTransport.PricesRequiringPermission);
            AddEnglishItemNamesForPriceConfigs(messages, _config.Vehicles.AttackHelicopter.PricesRequiringPermission);
            AddEnglishItemNamesForPriceConfigs(messages, _config.Vehicles.HotAirBalloon.PricesRequiringPermission);
            AddEnglishItemNamesForPriceConfigs(messages, _config.Vehicles.Rowboat.PricesRequiringPermission);
            AddEnglishItemNamesForPriceConfigs(messages, _config.Vehicles.RHIB.PricesRequiringPermission);
            AddEnglishItemNamesForPriceConfigs(messages, _config.Vehicles.SoloSub.PricesRequiringPermission);
            AddEnglishItemNamesForPriceConfigs(messages, _config.Vehicles.DuoSub.PricesRequiringPermission);

            lang.RegisterMessages(messages, this, "en");
        }

        #endregion
    }
}
