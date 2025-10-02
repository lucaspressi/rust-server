using Facepunch;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Plugins;
using UnityEngine;
using Oxide.Game.Rust.Libraries;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine.UI;

namespace Oxide.Plugins;

[Info("Server Rewards", "k1lly0u", "2.0.7")]
class ServerRewards : RustPlugin
{
    #region Fields

    [PluginReference]
    private Plugin Economics, HumanNPC, ImageLibrary, Kits, PlayerDLCAPI, PlaytimeTracker;

    private List<ItemDefinition> _sortedItemDefinitions;
    
    private readonly StringBuilder _stringBuilder = new StringBuilder();

    private static Func<string, string, string> _getMessage;

    private const string ADMIN_PERMISSION = "serverrewards.admin";

    #endregion

    #region Oxide Hooks

    private void Loaded()
    {
        LoadData();
        CreateCommonFilters();

        _getMessage = (key, userId) => lang.GetMessage(key, this, userId);
        
        permission.RegisterPermission(ADMIN_PERMISSION, this);
        _products.Data.RegisterPermissions(permission, this);

        Command command = Interface.Oxide.GetLibrary<Command>();
        command.AddChatCommand(Configuration.Options.StoreCommand, this, ChatOpenMenu);
        command.AddChatCommand(Configuration.Options.RPCommand, this, ChatPointsManagement);
        command.AddConsoleCommand(Configuration.Options.RPCommand, this, ConsolePointsManagement);
    }

    protected override void LoadDefaultMessages() => lang.RegisterMessages(_messages, this);

    private void OnServerInitialized()
    {
        CreateValidItemList();
        UpdateSellPriceList();
        RegisterImages();
        CreateKitsFunctions();
        
        if (!Configuration.Options.OwnedSkins)
            Debug.LogWarning("[ServerRewards] WARNING! As of August 7th 2025, granting access to paid skins that users do not own is against Rust's Terms of Service and can result in your server being delisted or worse.\n" +
                             "If you continue to allow users to use paid skins, you do so at your own risk!\n" +
                             "You can prevent users access to skins they do not own by enabling 'Only show/give players skins that they are allowed to use' in the config\n" +
                             "https://facepunch.com/legal/servers");
        else if (!PlayerDLCAPI)
            Debug.LogWarning("[ServerRewards] - PlayerDLCAPI plugin is not loaded, skin ownership checks will not work!");
        
        foreach (BasePlayer player in BasePlayer.activePlayerList)
            SendPointNotification(player);
    }

    private void OnPlayerConnected(BasePlayer player) => SendPointNotification(player);

    private void OnServerSave()
    {
        _balances.Save();
        _cooldowns.Save();
    }

    private void Unload()
    {
        _balances.Save();
        _cooldowns.Save();

        UIUser.OnUnload();
    }

    #endregion

    #region Functions

    private readonly string[] _ignoreItems = new string[] { "ammo.snowballgun", "blueprintbase", "rhib", "attackhelicopter", "motorbike", "motorbike_sidecar", "bicycle", "trike", "kayak", "rowboat", "tugboat", "skidoo", "minihelicopter.repair", "scraptransportheli.repair", "mlrs", "snowmobile", "spraycandecal", "vehicle.chassis", "vehicle.module", "water", "water.salt" };

    private void CreateValidItemList()
    {
        _sortedItemDefinitions ??= new List<ItemDefinition>(ItemManager.itemList.Count);
        
        foreach (ItemDefinition itemDefinition in ItemManager.itemList)
        {
            if (_ignoreItems.Contains(itemDefinition.shortname))
                continue;
            
            _sortedItemDefinitions.Add(itemDefinition);
        }
        
        _sortedItemDefinitions.Sort((a, b) =>
        {
            if (string.IsNullOrEmpty(a.displayName.english) && string.IsNullOrEmpty(b.displayName.english))
                return 0;
                
            if (string.IsNullOrEmpty(a.displayName.english))
                return 1;
                
            if (string.IsNullOrEmpty(b.displayName.english))
                return -1;
                
            return a.displayName.english.CompareTo(b.displayName.english);
        });
    }
    
    private void SendPointNotification(BasePlayer player)
    {
        if (!player)
            return;
       
        if (!_balances.Data.TryGetValue(player.userID, out int balance) || balance <= 0)
            return;

        _stringBuilder.Clear();
        
        _stringBuilder.AppendLine(_getMessage("Message.Title", player.UserIDString));
        _stringBuilder.AppendLine(string.Format(_getMessage("Message.Notification.Unspent", player.UserIDString), balance));
        _stringBuilder.AppendLine(Configuration.Options.NpcOnly ? _getMessage("Message.Notification.Unspent.NPC", player.UserIDString) : 
            string.Format(_getMessage("Message.Notification.Unspent.Command", player.UserIDString), Configuration.Options.StoreCommand));

        player.ChatMessage(_stringBuilder.ToString());
    }
    
    private static double CurrentTime() => DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds;

    private bool IsAdmin(BasePlayer player) => permission.UserHasPermission(player.UserIDString, ADMIN_PERMISSION) ||
                                               (player.net?.connection != null && player.net.connection.authLevel >= 2);

    private bool IsAdminOrConsole(ConsoleSystem.Arg arg)
    {
        if (arg.Connection == null)
            return true;
        
        BasePlayer player = arg.Player();
        if (!player)
            return false;

        return IsAdmin(player);
    }

    #endregion

    #region Chat Commands

    private bool OpenBlockedByPlugin(BasePlayer player)
    {
        object success = Interface.Call("canShop", player);
        if (success == null) 
            return false;
        
        string message = success as string ?? _getMessage("Message.ShopBlockedByPlugin", player.UserIDString);
            
        player.ChatMessage(message);
        return true;
    }

    private void ChatOpenMenu(BasePlayer player, string command, string[] args)
    {
        if (Configuration.Options.NpcOnly && !IsAdmin(player))
            return;

        if (OpenBlockedByPlugin(player))
            return;
        
        OpenStore(player);
    }
    
    #endregion 
    
    #region Admin Commands

    private void ChatPointsManagement(BasePlayer player, string command, string[] args)
    {
        if (!IsAdmin(player))
            return;

        if (args.Length >= 2)
        {
            bool isWildcard = args[1] == "*";
            
            IPlayer target = isWildcard ? null : covalence.Players.FindPlayer(args[1]);
            ulong targetID = target != null && ulong.TryParse(target.Id, out ulong u) ? u : 0UL;
            int amount = args.Length == 3 && int.TryParse(args[2], out int a) ? a : 0;

            if (!isWildcard)
            {
                if (target == null)
                {
                    player.ChatMessage("Player not found, please specify a valid username or user ID.");
                    return;
                }
                
                if (targetID == 0UL)
                {
                    player.ChatMessage("Player was found, but they have a invalid user ID.");
                    return;
                }
            }

            switch (args[0].ToLower())
            {
                case "add":
                    player.ChatMessage(CommandAddPoints(isWildcard, target, targetID, amount));
                    return;
                
                case "take":
                    player.ChatMessage(CommandTakePoints(isWildcard, target, targetID, amount));
                    return;
                
                case "clear":
                    player.ChatMessage(CommandClearPoints(isWildcard, target, targetID));
                    return;
                
                case "check":
                    if (isWildcard)
                    {
                        player.ChatMessage("You can not check RP using the '*' wildcard, please specify a valid username or user ID.");
                        return;
                    }
                    
                    player.ChatMessage(CommandCheckPoints(target, targetID));
                    return;
                
                default:
                    break;
            }
        }
        
        string hex = Configuration.UI.ButtonConfirmText.Hex;
        string cmd = Configuration.Options.RPCommand;
        
        player.ChatMessage($"<size=16><color=#{hex}>[{Title}]</color> Points Management</size>");
        player.ChatMessage($"<color=#{hex}>/{cmd} add <username|userid|*> <amount></color> - Add the specified amount of RP to a player");
        player.ChatMessage($"<color=#{hex}>/{cmd} take <username|userid|*> <amount></color> - Deduct the specified amount of RP from a player");
        player.ChatMessage($"<color=#{hex}>/{cmd} clear <username|userid|*></color> - Reset the specified players RP to 0");
        player.ChatMessage($"<color=#{hex}>/{cmd} check <username|userid></color> - Show the amount of RP a player has");
        player.ChatMessage($"Use the '<color=#{hex}>*</color>' wildcard in place of name or id to apply the command to all players.");
    }
    
    private bool ConsolePointsManagement(ConsoleSystem.Arg arg)
    {
        if (!IsAdminOrConsole(arg))
            return false;
        
        if (arg.Args is { Length: >= 2 })
        {
            bool isWildcard = arg.Args[1] == "*";
            
            IPlayer target = isWildcard ? null : covalence.Players.FindPlayer(arg.Args[1]);
            ulong targetID = target != null && ulong.TryParse(target.Id, out ulong u) ? u : 0UL;
            int amount = arg.Args.Length == 3 && int.TryParse(arg.Args[2], out int a) ? a : 0;

            if (!isWildcard)
            {
                if (target == null)
                {
                    SendReply(arg, "Player not found, please specify a valid username or user ID.");
                    return true;
                }
                
                if (targetID == 0UL)
                {
                    SendReply(arg, "Player was found, but they have a invalid user ID.");
                    return true;
                }
            }

            switch (arg.Args[0].ToLower())
            {
                case "add":
                    SendReply(arg, CommandAddPoints(isWildcard, target, targetID, amount));
                    return true;
                
                case "take":
                    SendReply(arg, CommandTakePoints(isWildcard, target, targetID, amount));
                    return true;
                
                case "clear":
                    SendReply(arg, CommandClearPoints(isWildcard, target, targetID));
                    return true;
                
                case "check":
                    if (isWildcard)
                    {
                        SendReply(arg, "You can not check RP using the '*' wildcard, please specify a valid username or user ID.");
                        return true;
                    }
                    
                    SendReply(arg, CommandCheckPoints(target, targetID));
                    return true;
                
                default:
                    break;
            }
        }
        
        SendReply(arg, $"{Configuration.Options.RPCommand} add <username|userid|*> <amount> - Add the specified amount of RP to a player");
        SendReply(arg, $"{Configuration.Options.RPCommand} take <username|userid|*> <amount> - Deduct the specified amount of RP from a player");
        SendReply(arg, $"{Configuration.Options.RPCommand} clear <username|userid|*> - Reset the specified players RP to 0");
        SendReply(arg, $"{Configuration.Options.RPCommand} check <username|userid> - Show the amount of RP a player has");
        SendReply(arg, "Use the '*' wildcard in place of name or id to apply the command to all players.");
        return true;
    }

    private string CommandAddPoints(bool isWildcard, IPlayer target, ulong targetID, int amount)
    {
        if (amount <= 0)
            return "Please specify a valid amount to add. Must be greater than 0.";

        if (isWildcard)
        {
            List<ulong> userIDs = Pool.Get<List<ulong>>();
            userIDs.AddRange(_balances.Data.Keys);
            foreach (ulong userID in userIDs)
            {
                _balances.Data[userID] += amount;
                SendPointsUpdated(userID);
            }
            Pool.FreeUnmanaged(ref userIDs);

            return $"You have added {amount}RP to every player!";
        }
        
        if (!_balances.Data.ContainsKey(targetID))
            _balances.Data[targetID] = amount;
        else _balances.Data[targetID] += amount;
        
        SendPointsUpdated(targetID);
        
        return $"You have added {amount}RP to {target.Name} ({target.Id})!";
    }
    
    private string CommandTakePoints(bool isWildcard, IPlayer target, ulong targetID, int amount)
    {
        if (amount <= 0)
            return "Please specify a valid amount to add. Must be greater than 0.";

        if (isWildcard)
        {
            List<ulong> userIDs = Pool.Get<List<ulong>>();
            userIDs.AddRange(_balances.Data.Keys);
            foreach (ulong userID in userIDs)
            {
                _balances.Data[userID] = Mathf.Max(_balances.Data[userID] - amount, 0);
                SendPointsUpdated(userID);
            }
            Pool.FreeUnmanaged(ref userIDs);

            return $"You have deducted {amount}RP from every player!";
        }
        
        if (!_balances.Data.ContainsKey(targetID))
            return $"Player {target.Name} ({target.Id}) does not have any RP to deduct from!";
        
        _balances.Data[targetID] = Mathf.Max(_balances.Data[targetID] - amount, 0);
        
        SendPointsUpdated(targetID);
        
        return $"You have deducted {amount}RP from {target.Name} ({target.Id})!";
    }
    
    private string CommandClearPoints(bool isWildcard, IPlayer target, ulong targetID)
    {
        if (isWildcard)
        {
            List<ulong> userIDs = Pool.Get<List<ulong>>();
            userIDs.AddRange(_balances.Data.Keys);
            foreach (ulong userID in userIDs)
            {
                _balances.Data[userID] = 0;
                SendPointsUpdated(userID);
            }
            Pool.FreeUnmanaged(ref userIDs);

            return "You have reset every players RP to 0!";
        }
        
        if (!_balances.Data.ContainsKey(targetID))
            return $"Player {target.Name} ({target.Id}) does not have any RP!";
        
        _balances.Data[targetID] = 0;
        
        SendPointsUpdated(targetID);
        
        return $"You have reset {target.Name}'s ({target.Id}) RP to 0!";
    }
    
    private string CommandCheckPoints(IPlayer target, ulong targetID) => 
        !_balances.Data.TryGetValue(targetID, out int balance) ? 
            $"Player {target.Name} ({target.Id}) does not have any RP!" : 
            $"{target.Name} ({target.Id}) has {balance}RP!";

    #endregion
    
    #region Image Library

    private const string MagnifyIconUrl = "https://chaoscode.io/oxide/Images/magnifyingglass.png";
    private const string SearchIconIdentifier = "srui.search";

    private static string _magnifyIcon;
    private static string _dataDirectory;

    private void RegisterImages()
    {
        if (!ImageLibrary)
        {
            Debug.LogWarning("[ServerRewards] ImageLibrary plugin not found, custom images will not be loaded...");
            return;
        }

        Dictionary<string, string> loadOrder = new Dictionary<string, string>();

        _dataDirectory = Path.Combine($"file://{Interface.Oxide.DataDirectory}", "ServerRewards", "Images");
        
        loadOrder[SearchIconIdentifier] = MagnifyIconUrl;
        
        foreach (Products.Item item in _products.Data.Items)
        {
            if (string.IsNullOrEmpty(item.IconURL))
                continue;
            
            string url = item.IconURL;
            if (!IsUrl(url))
                url = Path.Combine(_dataDirectory, url);
            
            loadOrder[item.IconURL] = url;
        }
        
        foreach (Products.Kit kit in _products.Data.Kits)
        {
            if (string.IsNullOrEmpty(kit.IconURL))
                continue;
            
            string url = kit.IconURL;
            if (!IsUrl(url))
                url = Path.Combine(_dataDirectory, url);
            
            loadOrder[kit.IconURL] = url;
        }
        
        foreach (Products.Command command in _products.Data.Commands)
        {
            if (string.IsNullOrEmpty(command.IconURL))
                continue;
            
            string url = command.IconURL;
            if (!IsUrl(url))
                url = Path.Combine(_dataDirectory, url);
            
            loadOrder[command.IconURL] = url;
        }
        
        ImageLibrary.Call("ImportImageList", Title, loadOrder, 0UL, false, new Action(OnImagesLoaded));
    }

    private void OnImagesLoaded() => _magnifyIcon = GetImage(SearchIconIdentifier);

    private void AddImage(string iconUrl)
    {
        string url = iconUrl;
        if (!IsUrl(url))
            url = Path.Combine(_dataDirectory, url);
        
        ImageLibrary?.Call("AddImage", url, iconUrl, 0UL);
    }
    
    private string GetImage(string name, ulong skinID = 0UL)
    {
        if (!ImageLibrary)
            return string.Empty;

        return ImageLibrary.Call<string>("GetImage", name, skinID);
    }
    
    private static bool IsUrl(string input) => Uri.TryCreate(input, UriKind.Absolute, out Uri result) && 
                                        (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps);

    #endregion
    
    #region Kits

    private static Func<string, bool> _isKit;
    private static Func<BasePlayer, string, object> _giveKit;
    private static Func<string, string> _getKitDescription;
    private static Func<string, string> _getKitImage;
    private static Func<string, JObject> _getKitData;

    private void CreateKitsFunctions()
    {
        _isKit = IsKit;
        _giveKit = GiveKit;
        _getKitDescription = GetKitDescription;
        _getKitImage = GetKitImage;
        _getKitData = GetKitObject;

    }
    
    private object GiveKit(BasePlayer player, string kit) => Kits?.Call("GiveKit", player, kit);

    private void GetAllKits(List<string> list) => Kits?.Call("GetAllKits", list);
    
    private bool IsKit(string name) => Kits && Kits.Call<bool>("IsKit", name);
    
    private string GetKitDescription(string name) => Kits ? Kits.Call<string>("GetKitDescription", name) : string.Empty;
    
    private string GetKitImage(string name) => Kits ? Kits.Call<string>("GetKitImage", name) : string.Empty;
    
    private JObject GetKitObject(string name) => Kits ? Kits.Call<JObject>("GetKitObject", name) : null;
    
    private class KitData
    {
        public ItemData[] MainItems { get; set; }
        public ItemData[] WearItems { get; set; }
        public ItemData[] BeltItems { get; set; }
        
        public class ItemData
        {
            public string Shortname { get; set; }
            public ulong SkinID { get; set; }
            public int Amount { get; set; }
            public bool IsBlueprint { get; set; }
        
            [JsonIgnore]
            private ItemDefinition _itemDefinition;
        
            [JsonIgnore]
            public ItemDefinition ItemDefinition
            {
                get
                {
                    if (!_itemDefinition)
                        _itemDefinition = ItemManager.FindItemDefinition(Shortname);
                    return _itemDefinition;
                }
            }
        }
    }
    
    #endregion
    
    #region PlayerDLCAPI
    
    private bool IsOwnedOrFreeItem(BasePlayer player, int itemID, ulong skinID)
    {
        if (PlayerDLCAPI != null && Configuration.Options.OwnedSkins)
            return PlayerDLCAPI.Call<bool>("IsOwnedOrFreeItem", player, itemID, skinID);
            
        return true;
    }
    
    private bool CanUseSkin(BasePlayer player, ulong skinID)
    {
        if (skinID == 0UL)
            return true;
            
        if (PlayerDLCAPI != null && Configuration.Options.OwnedSkins)
            return PlayerDLCAPI.Call<bool>("IsOwnedOrFreeSkin", player, skinID);
            
        return true;
    }

    private int GetRedirectedItemIdIfNotOwned(BasePlayer player, int itemId)
    {
        if (PlayerDLCAPI != null && Configuration.Options.OwnedSkins)
            return PlayerDLCAPI.Call<int>("GetRedirectedItemIdIfNotOwned", player, itemId);
            
        return itemId;
    }
    
    #endregion
    
    #region HumanNPC
    
    private void OnUseNPC(BasePlayer npc, BasePlayer player)
    {
        if (!player || !npc)
            return;

        if (!_npcStores.Data.TryGetValue(npc.userID, out NpcStore npcStore))
            return;
        
        if (OpenBlockedByPlugin(player))
            return;

        if (npcStore.Products != null)
        {
            UIUser user = UIUser.Get(player);
            user.NpcStore = npcStore;
        }

        OpenStore(player);
    }

    [ChatCommand("srnpc")]
    private void CommandNpc(BasePlayer player, string command, string[] args)
    {
        if (!IsAdmin(player))
            return;

        if (!HumanNPC)
        {
            player.ChatMessage("HumanNPC plugin not found, please install it to use this command.");
            return;
        }

        string hex = Configuration.UI.ButtonConfirmText.Hex;
        
        if (args.Length >= 1)
        {
            BasePlayer target = FindPlayer(player);
            if (!target)
            {
                player.ChatMessage("Look at the HumanNPC you want to interact with.");
                return;
            }

            if (!HumanNPC.Call<bool>("IsHumanNpc", target.userID.Get()))
            {
                player.ChatMessage("The player you are looking at it not a HumanNPC.");
                return;
            }
            
            _npcStores.Data.TryGetValue(target.userID, out NpcStore npcStore);

            switch (args[0].ToLower())
            {
                case "add":
                    if (npcStore != null)
                    {
                        player.ChatMessage("The NPC you are looking at is already a store NPC.");
                        return;
                    }
                    
                    _npcStores.Data[target.userID] = new NpcStore
                    {
                        Products = new Products(),
                        Navigation = new StoreNavigation(Configuration.Navigation),
                        CustomStore = false
                    };
                    _npcStores.Save();
                    
                    player.ChatMessage($"You have turned the NPC <color=#{hex}>{target.displayName}</color> into a store NPC.");
                    return;
                
                case "remove":
                    if (npcStore == null)
                    {
                        player.ChatMessage("The NPC you are looking at is not a store NPC.");
                        return;
                    }

                    _npcStores.Data.Remove(target.userID);
                    _npcStores.Save();
                    
                    player.ChatMessage($"You have removed the store from NPC <color=#{hex}>{target.displayName}</color>.");
                    return;
                
                case "setname":
                    if (npcStore == null)
                    {
                        player.ChatMessage("The NPC you are looking at is not a store NPC.");
                        return;
                    }
                    
                    if (args.Length < 2)
                    {
                        player.ChatMessage("Please specify a store name");
                        return;
                    }
                    
                    string name = string.Join(" ", args.Skip(1));
                    if (string.IsNullOrEmpty(name))
                    {
                        player.ChatMessage("Store name can not be empty.");
                        return;
                    }
                    
                    npcStore.Name = name;
                    _npcStores.Save();
                    
                    player.ChatMessage($"You have set the store name for NPC <color=#{hex}>{target.displayName}</color> to <color=#{hex}>{name}</color>.");
                    return;
                
                case "nav":
                    if (npcStore == null)
                    {
                        player.ChatMessage("The NPC you are looking at is not a store NPC.");
                        return;
                    }
                    
                    if (args.Length < 2)
                    {
                        player.ChatMessage($"Please specify a navigation type to toggle. Valid types: items, kits, commands, exchange, transfer, sell");
                        return;
                    }

                    if (!Enum.TryParse(args[1].ToLower(), true, out NavigationCategory category))
                    {
                        player.ChatMessage($"Invalid navigation type '{args[1]}'. Valid types: items, kits, commands, exchange, transfer, sell");
                        return;
                    }

                    bool result = false;
                    switch (category)
                    {
                        case NavigationCategory.Kits:
                            result = npcStore.Navigation.Kits = !npcStore.Navigation.Kits;
                            break;
                        case NavigationCategory.Commands:
                            result = npcStore.Navigation.Commands = !npcStore.Navigation.Commands;
                            break;
                        case NavigationCategory.Items:
                            result = npcStore.Navigation.Items = !npcStore.Navigation.Items;
                            break;
                        case NavigationCategory.Exchange:
                            result = npcStore.Navigation.Exchange = !npcStore.Navigation.Exchange;
                            break;
                        case NavigationCategory.Transfer:
                            result = npcStore.Navigation.Transfer = !npcStore.Navigation.Transfer;
                            break;
                        case NavigationCategory.Sell:
                            result = npcStore.Navigation.Seller = !npcStore.Navigation.Seller;
                            break;
                        default:
                            player.ChatMessage("Invalid navigation type. Valid types: items, kits, commands, exchange, transfer, sell");
                            break;
                    }
                    
                    _npcStores.Save();
                    player.ChatMessage($"Navigation type <color=#{hex}>{category}</color> for NPC <color=#{hex}>{target.displayName}</color> is now <color=#{hex}>{(result ? "enabled" : "disabled")}</color>.");
                    
                    return;
                
                case "customstore":
                    if (npcStore == null)
                    {
                        player.ChatMessage("The NPC you are looking at is not a store NPC.");
                        return;
                    }
                    
                    npcStore.CustomStore = !npcStore.CustomStore;
                    _npcStores.Save();
                    
                    player.ChatMessage($"Custom store mode for NPC <color=#{hex}>{target.displayName}</color> is now <color=#{hex}>{(npcStore.CustomStore ? "enabled" : "disabled")}</color>.");
                    return;
                
                default:
                    break;
            }
        }

        player.ChatMessage($"<size=16><color=#{hex}>[{Title}]</color> NPC Stores</size>");
        player.ChatMessage($"<color=#{hex}>/srnpc add</color> - Turn the NPC you are looking at into a store NPC");
        player.ChatMessage($"<color=#{hex}>/srnpc remove</color> - Remove the store from the NPC you are looking at");
        player.ChatMessage($"<color=#{hex}>/srnpc setname <name></color> - Sets a store name for the NPC you are looking at. This name is shown on the store page if custom store is enabled");
        player.ChatMessage($"<color=#{hex}>/srnpc nav <items|kits|commands|exchange|transfer|sell></color> - Toggles whether the specified navigation type is shown in the NPC store");
        player.ChatMessage($"<color=#{hex}>/srnpc customstore</color> - Toggles whether this NPC runs a custom store or just opens the global store");
    }
    
    private BasePlayer FindPlayer(BasePlayer player)
    {
        if (Physics.Raycast(player.eyes.HeadRay(), out RaycastHit raycastHit, 10f, 1 << (int)Rust.Layer.Player_Server))
            return raycastHit.collider.ToBaseEntity() as BasePlayer;
        return null;
    }
    
    #endregion

    #region UI

    private const string UI_MENU = "sr.menu";
    private const string UI_TOAST = "sr.toast";

    private void OpenStore(BasePlayer player)
    {
        UIUser user = UIUser.Get(player);

        Products products = user.NpcStore is { CustomStore: true } ? user.NpcStore.Products : _products.Data;
        StoreNavigation navigation = user.NpcStore != null ? user.NpcStore.Navigation : Configuration.Navigation;
        
        user.UpdateAvailableCategories(navigation, products, Economics);
        
        if (user.NpcStore != null && user.Category == NavigationCategory.None)
        {
            List<NavigationCategory> categories = user.AvailableCategories;
            if (categories.Count == 0)
            {
                user.Player.ChatMessage(user.Translate("Message.Error.NoCategories"));
                user.CloseMenu();
                return;
            }

            if (!categories.Contains(NavigationCategory.Items) && !categories.Contains(NavigationCategory.Kits) &&
                !categories.Contains(NavigationCategory.Commands) && !categories.Contains(NavigationCategory.Sell))
            {
                if (categories.Contains(NavigationCategory.Transfer))
                {
                    user.ExitToGame = true;
                    CreateTransferMenu(user);
                    return;
                }

                if (categories.Contains(NavigationCategory.Exchange))
                {
                    user.ExitToGame = true;
                    CreateExchangeMenu(user);
                    return;
                }
            }
        }

        CuiElementContainer container = UI.Container(Layer.Overlay, UI_MENU, Configuration.UI.Background, Anchor.FullStretch, Offset.zero);
        CreateHeader(container, user);

        switch (user.Category)
        {
            case NavigationCategory.Kits:
                CreateKits(container, user);
                break;
            case NavigationCategory.Commands:
                CreateCommands(container, user);
                break;
            case NavigationCategory.Items:
                CreateItems(container, user);
                break;
            case NavigationCategory.Sell:
                CreateSellItems(container, user);
                break;
            default:
                break;
        }

        CreateFooter(container, user);
        user.SendUI(container);
    }

    private void CreateHeader(CuiElementContainer container, UIUser user)
    {
        string storeName = user.NpcStore is { CustomStore: true } && !string.IsNullOrEmpty(user.NpcStore.Name) ? user.NpcStore.Name : user.Translate("UI.Title");
        
        const string UI_HEADER = "sr.header";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.TopStretch, new Offset(5f, -35f, -5f, -5f), UI_HEADER);
        UI.Text(container, UI_HEADER, storeName, Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), align: TextAnchor.MiddleLeft, size: 18);

        CreateNavigation(container, user, UI_HEADER);

        if (user.Category <= NavigationCategory.Items)
        {
            if (!string.IsNullOrEmpty(_magnifyIcon))
                UI.PNG(container, UI_HEADER, _magnifyIcon, Anchor.CenterRight, new Offset(-225f, -10f, -205f, 10f));

            const string UI_INPUT = "sr.search";
            UI.Panel(container, UI_HEADER, Configuration.UI.PanelSecondary, Anchor.CenterRight, new Offset(-200f, -10f, -30f, 10f), UI_INPUT);
            UI.Input(container, UI_INPUT, user.SearchFilter, $"{Commands.Search} {(int)ReturnType.Store}", Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f));
        }

        const string BUTTON_CLOSE = "sr.close";
        UI.Panel(container, UI_HEADER, Configuration.UI.ButtonReject, Anchor.CenterRight, new Offset(-25f, -10f, -5f, 10f), BUTTON_CLOSE);
        UI.Sprite(container, BUTTON_CLOSE, Sprites.Close, Configuration.UI.ButtonRejectText, Anchor.Center, new Offset(-7f, -7f, 7f, 7f));
        UI.Button(container, BUTTON_CLOSE, Commands.Close, Anchor.FullStretch, Offset.zero);
    }

    #region Navigation

    private void CreateNavigation(CuiElementContainer container, UIUser user, string parent)
    {
        List<NavigationCategory> categories = user.AvailableCategories;

        const string NAVIGATION = "sr.navigation";
        UI.Panel(container, parent, Colors.Clear, Anchor.FullStretch, new Offset(250f, 0f, -250f, 0f), NAVIGATION);

        if (user.Category == 0 && categories.Count > 0)
            user.Category = categories[0];

        const float WIDTH = 100f;
        const float SPACING = 5f;

        float totalWidth = (WIDTH * categories.Count) + (SPACING * (categories.Count - 1));
        float left = -(totalWidth * 0.5f);

        for (int i = 0; i < categories.Count; i++)
        {
            NavigationCategory category = categories[i];

            string buttonName = $"{NAVIGATION}.{category}";
            string buttonColor = user.Category == category ? Configuration.UI.ButtonConfirm : Configuration.UI.Button;
            string textColor = user.Category == category ? Configuration.UI.ButtonConfirmText : Configuration.UI.ButtonText;

            float xMin = left + (i * (WIDTH + SPACING));

            UI.Panel(container, NAVIGATION, buttonColor, Anchor.Center, new Offset(xMin, -10f, xMin + WIDTH, 10f), buttonName);
            UI.Text(container, buttonName, user.Translate($"UI.Category.{category.ToString()}"), Anchor.FullStretch, Offset.zero, color: textColor);
            UI.Button(container, buttonName, $"{Commands.Navigation} {(int)category}", Anchor.FullStretch, Offset.zero);
        }
    }

    public enum NavigationCategory
    {
        None = 0,
        Kits,
        Commands,
        Items,
        Sell,
        Transfer,
        Exchange
    }

    #endregion

    #region Footer

    private void CreateFooter(CuiElementContainer container, UIUser user)
    {
        const string FOOTER = "sr.footer";

        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.BottomStretch, new Offset(5f, 5f, -5f, 35f), FOOTER);
        
        // Admin Toggle
        if (IsAdmin(user.Player))
        {
            const string TOGGLE = "sr.admin.toggle";
            string buttonColor = user.AdminMode ? Configuration.UI.ButtonConfirm : Configuration.UI.Button;
            string textColor = user.AdminMode ? Configuration.UI.ButtonConfirmText : Configuration.UI.ButtonText;
            
            UI.Panel(container, FOOTER, buttonColor, Anchor.CenterLeft, new Offset(5f, -10f, 135f, 10f), TOGGLE);
            UI.Text(container, TOGGLE, user.Translate("UI.AdminMode"), Anchor.FullStretch, Offset.zero, color: textColor);
            UI.Button(container, TOGGLE, Commands.AdminToggle, Anchor.FullStretch, Offset.zero);

            if (user.AdminMode && user.Category <= NavigationCategory.Items)
            {
                const string ADD_PRODUCT = "sr.admin.addproduct";
                UI.Panel(container, FOOTER, Configuration.UI.ButtonConfirm, Anchor.Center, new Offset(-75f, -10f, 75f, 10f), ADD_PRODUCT);
                UI.Sprite(container, ADD_PRODUCT, Sprites.Authorize, Configuration.UI.ButtonConfirmText, Anchor.CenterLeft, new Offset(7f, -6f, 19f, 6f));
                UI.Text(container, ADD_PRODUCT, user.Translate("UI.AddProduct"), Anchor.FullStretch, new Offset(11f, 0f, 0f, 0f), color: textColor);
                UI.Button(container, ADD_PRODUCT, Commands.AddProduct, Anchor.FullStretch, Offset.zero);
            }
        }

        const string BALANCE = "sr.balance";
        _balances.Data.TryGetValue(user.UserId, out int balance);
        UI.Panel(container, FOOTER, Configuration.UI.ButtonConfirm, Anchor.CenterRight, new Offset(-130f, -10f, -5f, 10f), BALANCE);
        UI.Text(container, BALANCE, user.Translate("UI.RP", balance), Anchor.FullStretch, Offset.zero, color: Configuration.UI.ButtonConfirmText);

        if (Configuration.UI.ShowPlaytime && PlaytimeTracker != null)
        {
            object t = Interface.CallHook("GetPlayTime", user.Player.UserIDString);
            if (t is double time)
            {
                const string PLAYTIME = "sr.playtime";
                UI.Panel(container, FOOTER, Configuration.UI.ButtonPurchase, Anchor.CenterRight, new Offset(-280f, -10f, -135f, 10f), PLAYTIME);
                UI.Countdown(container, PLAYTIME, user.Translate("UI.Playtime", "%TIME_LEFT%"), (int)time, int.MaxValue, Anchor.FullStretch, Offset.zero, color: Configuration.UI.ButtonPurchaseText);
            }
        }
    }

    #endregion

    #region Items

    private void CreateItems(CuiElementContainer container, UIUser user)
    {
        Products products = user.NpcStore is { CustomStore: true } ? user.NpcStore.Products : _products.Data;
        
        const string ITEMS = "sr.items";
        UI.Panel(container, UI_MENU, Colors.Clear, Anchor.FullStretch, new Offset(5f, 40f, -5f, -40f), ITEMS);
        CreateItemsNavigation(container, ITEMS, user, products);
        CreateItemsLayout(container, ITEMS, user, products);
    }

    private void CreateItemsNavigation(CuiElementContainer container, string parent, UIUser user, Products products)
    {
        const string NAVIGATION = "sr.items.navigation";
        UI.Panel(container, parent, Configuration.UI.PanelPrimary, Anchor.LeftStretch, new Offset(0f, 0f, 140f, 0f), NAVIGATION);

        const float HEIGHT = 20f;
        const float SPACING = 5f;

        for (int i = 0; i < products.ItemCategories.Length; i++)
        {
            ItemCategory category = products.ItemCategories[i];
           
            string buttonName = $"{NAVIGATION}.{category}";
            string buttonColor = user.ItemCategory == category ? Configuration.UI.ButtonConfirm : Configuration.UI.Button;
            string textColor = user.ItemCategory == category ? Configuration.UI.ButtonConfirmText : Configuration.UI.ButtonText;

            float yMax = -5f - (i * (HEIGHT + SPACING));

            UI.Panel(container, NAVIGATION, buttonColor, Anchor.TopStretch, new Offset(5f, yMax - HEIGHT, -5f, yMax), buttonName);
            UI.Text(container, buttonName, user.Translate($"UI.ItemCategory.{category.ToString()}"), Anchor.FullStretch, Offset.zero, color: textColor);
            UI.Button(container, buttonName, $"{Commands.ItemCategory} {(int)category}", Anchor.FullStretch, Offset.zero);
        }
    }

    private void CreateItemsLayout(CuiElementContainer container, string parent, UIUser user, Products products)
    {
        const string LAYOUT = "sr.items.layout";

        UI.Panel(container, parent, Configuration.UI.PanelPrimary, Anchor.FullStretch, new Offset(145f, 0f, 0f, 0f), LAYOUT);
        UI.Text(container, LAYOUT, user.Translate("UI.Items"), Anchor.FullStretch, Offset.zero, color: Colors.BarelyVisible, size: 120);
        
        const string SCROLL = "sr.items.scroll";
        CuiScrollbar scrollbar = UI.Scrollbar(
            Configuration.UI.ScrollbarHandle, Configuration.UI.ScrollbarHighlight, Configuration.UI.ScrollbarPressed, Configuration.UI.Scrollbar);
        
        CuiRectTransformComponent contentRect = UI.ScrollView(container, LAYOUT, Anchor.FullStretch, new Offset(5f, 5f, -5f, -5f), 
            Anchor.FullStretch, Offset.zero, null, scrollbar, SCROLL);
        
        int count = 0;
        
        _itemsGrid ??= new HorizontalGrid(new Vector2(133f, 155.5f), new Vector2(5f, 5f), 8, 630f);
        
        _balances.Data.TryGetValue(user.UserId, out int balance);

        List<Products.Item> items = Pool.Get<List<Products.Item>>();
        FilterList(products.Items, items, user, _itemProductsContainsFilter);
        
        items.Sort((a, b) =>
        {
            ItemDefinition aDefinition = a.ItemDefinition;
            ItemDefinition bDefinition = b.ItemDefinition;
            
            if (!aDefinition && !bDefinition)
                return 0;
            
            if (!aDefinition)
                return 1;
            
            if (!bDefinition)
                return -1;
            
            string aName = string.IsNullOrEmpty(a.DisplayName) ? aDefinition.displayName.english : a.DisplayName;
            string bName = string.IsNullOrEmpty(b.DisplayName) ? bDefinition.displayName.english : b.DisplayName;
            
            return aName.CompareTo(bName);
        });

        for (int index = 0; index < items.Count; index++)
        {
            Products.Item item = items[index];
            if (item.Category != user.ItemCategory && user.ItemCategory != ItemCategory.All)
                continue;
            
            if (!string.IsNullOrEmpty(item.Permission) && !permission.UserHasPermission(user.Player.UserIDString, item.Permission) && !IsAdmin(user.Player))
                continue;
            
            ItemDefinition itemDefinition = item.ItemDefinition;
            if (!itemDefinition)
                continue;

            if (Configuration.Options.HideDlc && !user.AdminMode && !IsOwnedOrFreeItem(user.Player, itemDefinition.itemid, item.SkinId))
                continue;

            // Item container
            string itemName = $"{LAYOUT}.{item.ID}";
            UI.Panel(container, SCROLL, Configuration.UI.PanelSecondary, Anchor.TopLeft, _itemsGrid.Get(count), itemName);
            
            CreateItemElement(container, itemName, item, user, balance);

            count++;
        }
        
        Pool.FreeUnmanaged(ref items);

        _itemsGrid.ResizeToFit(count, contentRect);
    }
    
    private void CreateItemElement(CuiElementContainer container, string parent, Products.Item item, UIUser user, int balance)
    {
        bool isDlcItem = !item.IgnoreDlcCheck && !Configuration.Options.HideDlc && !IsOwnedOrFreeItem(user.Player, item.ItemDefinition.itemid, item.SkinId);
        
        string iconName = $"{parent}.icon";
        UI.Panel(container, parent, Colors.Clear, Anchor.TopCenter, new Offset(-64f, -130.5f, 64f, -2.5f), iconName);
        
        if (!string.IsNullOrEmpty(item.IconURL))
            UI.PNG(container, iconName, GetImage(item.IconURL), Anchor.FullStretch, new Offset(16f, 8f, -16f, -24f));
        else UI.Icon(container, iconName, item.ItemDefinition.itemid, item.SkinId, Anchor.FullStretch, new Offset(16f, 8f, -16f, -24f));
        
        string name = string.IsNullOrEmpty(item.DisplayName) ? item.ItemDefinition.displayName.english : item.DisplayName;
        
        string titleName = $"{parent}.title";
        UI.Panel(container, iconName, Colors.BarelyVisibleDark, Anchor.TopStretch, new Offset(0f, -20f, 0f, 0f), titleName);
        UI.Text(container, titleName, name, Anchor.FullStretch, Offset.zero, size: 12);
        
        if (item.IsBp)
            UI.Icon(container, iconName, Products.Item.BlueprintBase.itemid, 0UL, Anchor.BottomLeft, new Offset(2.5f, 2.5f, 26.5f, 26.5f));

        if (item.Amount > 1)
            UI.Text(container, iconName, $"x{item.Amount}", Anchor.BottomStretch, new Offset(2.5f, 0f, -2.5f, 20f), align: TextAnchor.LowerRight);

        if (user.AdminMode)
            CreateAdminButtons(container, parent, user, ProductType.Item, item.ID);
        else CreatePurchaseButton(container, parent, user, ProductType.Item, item.ID, item.Cost, balance, isDlcItem);
    }

    #endregion
    
    #region Kits

    private void CreateKits(CuiElementContainer container, UIUser user)
    {
        Products products = user.NpcStore is { CustomStore: true } ? user.NpcStore.Products : _products.Data;

        const string KITS = "sr.kits";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.FullStretch, new Offset(5f, 40f, -5f, -40f), KITS);
        UI.Text(container, KITS, user.Translate("UI.Kits"), Anchor.FullStretch, Offset.zero, color: Colors.BarelyVisible, size: 120);
        
        const string SCROLL = "sr.kits.scroll";
        CuiScrollbar scrollbar = UI.Scrollbar(
            Configuration.UI.ScrollbarHandle, Configuration.UI.ScrollbarHighlight, Configuration.UI.ScrollbarPressed, Configuration.UI.Scrollbar);
        
        CuiRectTransformComponent contentRect = UI.ScrollView(container, KITS, Anchor.FullStretch, new Offset(5f, 5f, -5f, -5f), 
            Anchor.FullStretch, Offset.zero, null, scrollbar, SCROLL);
        
        int count = 0;

        _kitsGrid ??= new HorizontalGrid(new Vector2(307.5f, 361.5f), new Vector2(5f, 5f), 4, 630f);
        
        _balances.Data.TryGetValue(user.UserId, out int balance);

        List<Products.Kit> kits = Pool.Get<List<Products.Kit>>();
        FilterList(products.Kits, kits, user, _kitProductsContainsFilter);
        
        kits.Sort((a, b) => a.DisplayName.CompareTo(b.DisplayName));

        for (int index = 0; index < kits.Count; index++)
        {
            Products.Kit kit = kits[index];
            if (!IsKit(kit.KitName))
                continue;
            
            if (!string.IsNullOrEmpty(kit.Permission) && !permission.UserHasPermission(user.Player.UserIDString, kit.Permission) && !IsAdmin(user.Player))
                continue;

            string kitName = $"sr.kits.{kit.ID}";
            UI.Panel(container, SCROLL, Configuration.UI.PanelSecondary, Anchor.TopLeft, _kitsGrid.Get(count), kitName);
            
            if (CreateKitElement(container, kitName, kit, user, balance))
                count++;
        }
        
        Pool.FreeUnmanaged(ref kits);

        _kitsGrid.ResizeToFit(count, contentRect);
    }

    private bool CreateKitElement(CuiElementContainer container, string parent, Products.Kit kit, UIUser user, int balance)
    {
        KitData kitData = kit.KitData;

        if (kitData == null)
            return false;

        string icon = !string.IsNullOrEmpty(kit.IconURL) ? GetImage(kit.IconURL) : string.Empty;
        bool hasIcon = !string.IsNullOrEmpty(icon);
        
        UI.Text(container, parent, kit.DisplayName, Anchor.TopStretch, new Offset(5f, -22.5f, hasIcon ? -55f : -5f, -2.5f), size: 12, align: TextAnchor.MiddleLeft);
        
        if (!string.IsNullOrEmpty(kit.Description))
            UI.Text(container, parent, kit.Description, Anchor.TopStretch, new Offset(5f, -55f, hasIcon ? -55f : -5f, -25f), size: 10, align: TextAnchor.UpperLeft);
        
        if (hasIcon)
            UI.PNG(container, parent, GetImage(kit.IconURL), Anchor.TopRight, new Offset(-52.5f, -52.5f, -2.5f, -2.5f));
        
        _kitsBeltGrid ??= new HorizontalGrid(new Vector2(32, 32), new Vector2(5f, 5f), 8, 0f);
        _kitsWearGrid ??= new HorizontalGrid(new Vector2(32, 32), new Vector2(5f, 5f), 8, 0f);
        _kitsMainGrid ??= new HorizontalGrid(new Vector2(32, 32), new Vector2(5f, 5f), 6, 0f);
        
        // Belt Layout
        string beltName = $"{parent}.belt";
        UI.Panel(container, parent, Colors.Clear, Anchor.TopStretch, new Offset(2.5f, -110.75f, -2.5f, -56.25f), beltName);
        UI.Panel(container, beltName, Colors.BarelyVisibleDark, Anchor.TopStretch, new Offset(0f, -20f, 0f, 0f));
        UI.Text(container, beltName, user.Translate("UI.Kit.Belt"), Anchor.TopStretch, new Offset(5f, -20f, 5f, 0f), size: 12, align: TextAnchor.MiddleLeft);
        LayoutKitContainer(container, beltName, user, kitData.BeltItems, 6, _kitsBeltGrid);
        
        // Wear Layout
        string wearName = $"{parent}.wear";
        UI.Panel(container, parent, Colors.Clear, Anchor.TopStretch, new Offset(2.5f, -167.75f, -2.5f, -113.25f), wearName);
        UI.Panel(container, wearName, Colors.BarelyVisibleDark, Anchor.TopStretch, new Offset(0f, -20f, 0f, 0f));
        UI.Text(container, wearName, user.Translate("UI.Kit.Wear"), Anchor.TopStretch, new Offset(5f, -20f, 5f, 0f), size: 12, align: TextAnchor.MiddleLeft);
        LayoutKitContainer(container, wearName, user, kitData.WearItems, 8, _kitsWearGrid);
        
        // Main Layout
        string mainName = $"{parent}.main";
        UI.Panel(container, parent, Colors.Clear, Anchor.TopStretch, new Offset(2.5f, -338.25f, -2.5f, -170.25f), mainName);
        UI.Panel(container, mainName, Colors.BarelyVisibleDark, Anchor.TopStretch, new Offset(0f, -20f, 0f, 0f));
        UI.Text(container, mainName, user.Translate("UI.Kit.Main"), Anchor.TopStretch, new Offset(5f, -20f, 5f, 0f), size: 12, align: TextAnchor.MiddleLeft);
        LayoutKitContainer(container, mainName, user, kitData.MainItems, 24, _kitsMainGrid);

        if (user.AdminMode)
            CreateAdminButtons(container, parent, user, ProductType.Kit, kit.ID);
        else CreatePurchaseButton(container, parent, user, ProductType.Kit, kit.ID, kit.Cost, balance);

        return true;
    }

    private void LayoutKitContainer(CuiElementContainer container, string parent, UIUser user, KitData.ItemData[] items, int minCount, HorizontalGrid grid)
    {
        string layout = $"{parent}.layout";
        UI.Panel(container, parent, Colors.Clear, Anchor.FullStretch, new Offset(2.5f, 0f, -2.5f, -22.5f), layout);
        
        for (int i = 0; i < minCount; i++)
        {
            KitData.ItemData itemData = items != null && i < items.Length ? items[i] : null;
            
            string item = $"{layout}.{i}";
            Offset offset = grid.Get(i);
            
            UI.Panel(container, layout, Colors.BarelyVisibleDark, Anchor.TopLeft, offset, item);

            if (itemData != null)
            {
                ItemDefinition itemDefinition = itemData.ItemDefinition;
                if (!itemDefinition)
                    continue;
                
                int itemId = GetRedirectedItemIdIfNotOwned(user.Player, itemDefinition.itemid);
                ulong skinId = CanUseSkin(user.Player, itemData.SkinID) ? itemData.SkinID : 0UL;
                
                UI.Icon(container, item, itemId, skinId, Anchor.FullStretch, Offset.zero);
                
                if (itemData.Amount > 1)
                    UI.Text(container, item, $"x{itemData.Amount}", Anchor.FullStretch, Offset.zero, size: 8, align: TextAnchor.LowerRight);
                
                if (itemData.IsBlueprint)
                    UI.Icon(container, item, Products.Item.BlueprintBase.itemid, 0UL, Anchor.TopLeft, new Offset(0f, -8f, -8f, 0));
            }
        }
    }

    #endregion
    
    #region Commands

    private void CreateCommands(CuiElementContainer container, UIUser user)
    {
        Products products = user.NpcStore is { CustomStore: true } ? user.NpcStore.Products : _products.Data;

        const string COMMANDS = "sr.commands";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.FullStretch, new Offset(5f, 40f, -5f, -40f), COMMANDS);
        UI.Text(container, COMMANDS, user.Translate("UI.Commands"), Anchor.FullStretch, Offset.zero, color: Colors.BarelyVisible, size: 120);
        
        const string SCROLL = "sr.commands.scroll";
        CuiScrollbar scrollbar = UI.Scrollbar(
            Configuration.UI.ScrollbarHandle, Configuration.UI.ScrollbarHighlight, Configuration.UI.ScrollbarPressed, Configuration.UI.Scrollbar);
        
        CuiRectTransformComponent contentRect = UI.ScrollView(container, COMMANDS, Anchor.FullStretch, new Offset(5f, 5f, -5f, -5f), 
            Anchor.FullStretch, Offset.zero, null, scrollbar, SCROLL);
        
        int count = 0;

        _commandsGrid ??= new HorizontalGrid(new Vector2(411.6f, 90f), new Vector2(5f, 5f), 3, 630f);
        
        _balances.Data.TryGetValue(user.UserId, out int balance);

        List<Products.Command> commands = Pool.Get<List<Products.Command>>();
        FilterList(products.Commands, commands, user, _commandProductsContainsFilter);
        
        commands.Sort((a, b) => a.DisplayName.CompareTo(b.DisplayName));

        for (int index = 0; index < commands.Count; index++)
        {
            Products.Command command = commands[index];
            if (!string.IsNullOrEmpty(command.Permission) && !permission.UserHasPermission(user.Player.UserIDString, command.Permission) && !IsAdmin(user.Player))
                continue;
            
            string commandName = $"sr.commands.{command.ID}";
            UI.Panel(container, SCROLL, Configuration.UI.PanelSecondary, Anchor.TopLeft, _commandsGrid.Get(count), commandName);

            CreateCommandElement(container, commandName, command, user, balance);
            count++;
        }
        
        Pool.FreeUnmanaged(ref commands);

        _commandsGrid.ResizeToFit(count, contentRect);
    }
    
    private void CreateCommandElement(CuiElementContainer container, string parent, Products.Command command, UIUser user, int balance)
    {
        string icon = !string.IsNullOrEmpty(command.IconURL) ? GetImage(command.IconURL) : string.Empty;
        bool hasIcon = !string.IsNullOrEmpty(icon);
        
        UI.Text(container, parent, command.DisplayName, Anchor.TopStretch, new Offset(5f, -22.5f, hasIcon ? -65f : -5f, -2.5f), size: 12, align: TextAnchor.MiddleLeft);
        
        if (!string.IsNullOrEmpty(command.Description))
            UI.Text(container, parent, command.Description, Anchor.TopStretch, new Offset(5f, -65f, hasIcon ? -65f : -5f, -22.5f), size: 10, align: TextAnchor.UpperLeft);
        
        if (hasIcon)
            UI.PNG(container, parent, GetImage(command.IconURL), Anchor.TopRight, new Offset(-62.5f, -62.5f, -2.5f, -2.5f));
        
        if (user.AdminMode)
            CreateAdminButtons(container, parent, user, ProductType.Command, command.ID);
        else CreatePurchaseButton(container, parent, user, ProductType.Command, command.ID, command.Cost, balance);
    }
    
    #endregion
    
    #region Transfer
    
    private void CreateTransferMenu(UIUser user)
    {
        BasePlayer to = user.TransferTarget;
        
        CuiElementContainer container = UI.Container(Layer.Overlay, UI_MENU, Configuration.UI.Background, Anchor.FullStretch, Offset.zero);

        // Header
        const string HEADER = "sr.transfer.header";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, 32.5f, 150f, 62.5f), HEADER);
        UI.Text(container, HEADER, user.Translate("UI.Transfer.Title"), Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), size: 18, align: TextAnchor.MiddleLeft);
        
        // Info
        const string CONTENTS = "sr.transfer.contents";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, -27.5f, 150f, 27.5f), CONTENTS);
        
        // User Select
        UI.Text(container, CONTENTS, user.Translate("UI.User"), Anchor.TopStretch, new Offset(5f, -25f, -74f, -5f), size: 12, align: TextAnchor.MiddleLeft);
        UI.Panel(container, CONTENTS, Colors.BarelyVisibleDark, Anchor.TopStretch, new Offset(100f, -25f, -5f, -5f));
        UI.Text(container, CONTENTS, to ? to.displayName : "", Anchor.TopStretch, new Offset(105f, -25f, -85f, -5f), size: 12, align: TextAnchor.MiddleLeft);
        UI.Button(container, CONTENTS, $"{Commands.OpenSelector} {(int)ReturnType.SelectTransferTarget}", Anchor.TopStretch, new Offset(100f, -25f, -5f, -5f));
        
        // Amount
        UI.Text(container, CONTENTS, user.Translate("UI.Amount"), Anchor.TopStretch, new Offset(5f, -50f, -74f, -30f), size: 12, align: TextAnchor.MiddleLeft);
        UI.Panel(container, CONTENTS, Colors.BarelyVisibleDark, Anchor.TopStretch, new Offset(100f, -50f, -5f, -30f));
        UI.Input(container, CONTENTS, user.TransferAmount.ToString(), $"{Commands.Transfer} {(to ? to.UserIDString : "0")}", Anchor.TopStretch, new Offset(105f, -50f, -10f, -30f), size: 12, align: TextAnchor.MiddleLeft);
        
        // Footer
        const string FOOTER = "sr.transfer.footer";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, -62.5f, 150f, -32.5f), FOOTER);
        
        const string CANCEL = "sr.transfer.cancel";
        UI.Panel(container, FOOTER, Configuration.UI.ButtonReject, Anchor.CenterLeft, new Offset(5f, -10f, 135f, 10f), CANCEL);
        UI.Text(container, CANCEL, user.Translate("UI.Cancel"), Anchor.FullStretch, Offset.zero, color: Configuration.UI.ButtonRejectText);
        UI.Button(container, CANCEL, user.ExitToGame ? Commands.Close : Commands.ReturnToStore, Anchor.FullStretch, Offset.zero);
        
        const string CONFIRM = "sr.transfer.confirm";
        bool valid = to && user.TransferAmount > 0;
        UI.Panel(container, FOOTER, valid ? Configuration.UI.ButtonConfirm : Configuration.UI.ButtonDisabled, Anchor.CenterRight, new Offset(-160f, -10f, -5f, 10f), CONFIRM);
        UI.Text(container, CONFIRM, user.Translate("UI.Transfer.Confirm", user.TransferAmount), Anchor.FullStretch, Offset.zero, color: valid ? Configuration.UI.ButtonConfirmText : Configuration.UI.ButtonDisabledText);
        UI.Button(container, CONFIRM, valid ? $"{Commands.ConfirmTransfer} {(to ? to.UserIDString : "0")} {user.TransferAmount}" : "", Anchor.FullStretch, Offset.zero);
        
        user.SendUI(container);
    }
    
    private void CreatePlayerSelector(UIUser user)
    {
        List<BasePlayer> players = Pool.Get<List<BasePlayer>>();
        players.AddRange(BasePlayer.activePlayerList);
        players.Remove(user.Player);
        FilterList(players, user, (phrase, p) => p.displayName.Contains(phrase, CompareOptions.OrdinalIgnoreCase));
        CreateSelector(user, "UI.SelectPlayer", ReturnType.SelectTransferTarget, players, DrawPlayerSelector);
        Pool.FreeUnmanaged(ref players);
    }

    private bool DrawPlayerSelector(CuiElementContainer container, string parent, UIUser user, float height, float spacing, int count, BasePlayer player)
    {
        if (!player)
            return false;
        
        bool isSelected = player == user.TransferTarget;
        
        string buttonColor = isSelected ? Configuration.UI.ButtonConfirm : Configuration.UI.Button;
        string textColor = isSelected ? Configuration.UI.ButtonConfirmText : Configuration.UI.ButtonText;
            
        float yMax = -(count * (height + spacing));

        string panelName = player.UserIDString;
        UI.Panel(container, parent, buttonColor, Anchor.TopStretch, new Offset(0f, yMax - height, -5f, yMax), panelName);
        UI.Text(container, panelName, player.displayName, Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), color: textColor, align: TextAnchor.MiddleLeft);
        UI.Button(container, panelName, $"{Commands.Transfer} {player.UserIDString}", Anchor.FullStretch, Offset.zero);
            
        return true;
    }
    
    #endregion
    
    #region Exchange
    
    private enum ExchangeType { None, RP, Economics}
    private void CreateExchangeMenu(UIUser user, int rp = 0, int economics = 0)
    {
        CuiElementContainer container = UI.Container(Layer.Overlay, UI_MENU, Configuration.UI.Background, Anchor.FullStretch, Offset.zero);

        // Header
        const string HEADER = "sr.exchange.header";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, 45f, 150f, 75f), HEADER);
        UI.Text(container, HEADER, user.Translate("UI.Exchange.Title"), Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), size: 18, align: TextAnchor.MiddleLeft);
        
        const string BUTTON_CLOSE = "sr.exchange.close";
        UI.Panel(container, HEADER, Configuration.UI.ButtonReject, Anchor.CenterRight, new Offset(-25f, -10f, -5f, 10f), BUTTON_CLOSE);
        UI.Sprite(container, BUTTON_CLOSE, Sprites.Close, Configuration.UI.ButtonRejectText, Anchor.Center, new Offset(-7f, -7f, 7f, 7f));
        UI.Button(container, BUTTON_CLOSE, user.ExitToGame ? Commands.Close : Commands.ReturnToStore, Anchor.FullStretch, Offset.zero);
        
        const string CONTENTS = "sr.exchange.contents";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, -40f, 150f, 40f), CONTENTS);
        
        UI.Text(container, CONTENTS, user.Translate("UI.Exchange.Rate", Configuration.Options.ExchangeRate), Anchor.TopStretch, new Offset(5f, -25f, -5f, -5f), size: 12, align: TextAnchor.MiddleLeft, color: Colors.TransparentWhite);
        // RP to Economics
        const string RP2EC = "sr.exchange.rp";
        UI.Panel(container, CONTENTS, Colors.Clear, Anchor.TopStretch, new Offset(5f, -50f, -5f, -30f), RP2EC);
        
        const string RP2EC_INPUT = "sr.exchange.rp.input";
        UI.Panel(container, RP2EC, Colors.BarelyVisibleDark, Anchor.FullStretch, new Offset(0f, 0f, -215f, 0f), RP2EC_INPUT);
        UI.Input(container, RP2EC_INPUT, rp.ToString(), $"{Commands.Exchange} {rp} {economics} {(int)ExchangeType.RP}", Anchor.FullStretch, new Offset(5f, 0f, -20f, 0f), size: 12);
        UI.Text(container, RP2EC_INPUT, user.Translate("UI.Exchange.RP"), Anchor.FullStretch, new Offset(55f, 0f, 0f, 0f), size: 12, align: TextAnchor.MiddleCenter);
        
        UI.Text(container, RP2EC, user.Translate("UI.Exchange.RPToEconomics", ConvertRPToEconomics(rp)), Anchor.FullStretch, new Offset(80f, 0f, -85f, 0f), size: 12, align: TextAnchor.MiddleLeft);
        
        const string RP2EC_BUTTON = "sr.exchange.rp.button";
        UI.Panel(container, RP2EC, Configuration.UI.ButtonConfirm, Anchor.FullStretch, new Offset(210f, 0f, 0f, 0f), RP2EC_BUTTON);
        UI.Text(container, RP2EC_BUTTON, user.Translate("UI.Exchange.Convert"), Anchor.FullStretch, Offset.zero, color: Configuration.UI.ButtonConfirmText, size: 12, align: TextAnchor.MiddleCenter);
        UI.Button(container, RP2EC_BUTTON, $"{Commands.ConvertToEconomics} {rp}", Anchor.FullStretch, Offset.zero);
        
        // Economics to RP
        const string EC2RP = "sr.exchange.eco";
        UI.Panel(container, CONTENTS, Colors.Clear, Anchor.TopStretch, new Offset(5f, -75f, -5f, -55f), EC2RP);
        
        const string EC2RP_INPUT = "sr.exchange.eco.input";
        UI.Panel(container, EC2RP, Colors.BarelyVisibleDark, Anchor.FullStretch, new Offset(0f, 0f, -215f, 0f), EC2RP_INPUT);
        UI.Input(container, EC2RP_INPUT, economics.ToString(), $"{Commands.Exchange} {rp} {economics} {(int)ExchangeType.Economics}", Anchor.FullStretch, new Offset(5f, 0f, -20f, 0f), size: 12);
        UI.Text(container, EC2RP_INPUT, user.Translate("UI.Exchange.Economics"), Anchor.FullStretch, new Offset(55f, 0f, 0f, 0f), size: 12, align: TextAnchor.MiddleCenter);
        
        UI.Text(container, EC2RP, user.Translate("UI.Exchange.EconomicsToRP", ConvertEconomicsToRP(economics)), Anchor.FullStretch, new Offset(80f, 0f, -85f, 0f), size: 12, align: TextAnchor.MiddleLeft);
        
        const string EC2RP_BUTTON = "sr.exchange.eco.button";
        UI.Panel(container, EC2RP, Configuration.UI.ButtonConfirm, Anchor.FullStretch, new Offset(210f, 0f, 0f, 0f), EC2RP_BUTTON);
        UI.Text(container, EC2RP_BUTTON, user.Translate("UI.Exchange.Convert"), Anchor.FullStretch, Offset.zero, color: Configuration.UI.ButtonConfirmText, size: 12, align: TextAnchor.MiddleCenter);
        UI.Button(container, EC2RP_BUTTON, $"{Commands.ConvertToRP} {economics}", Anchor.FullStretch, Offset.zero);

        _balances.Data.TryGetValue(user.UserId, out int rpBalance);
        double economicsBalance = Economics?.Call<double>("Balance", user.Player.UserIDString) ?? 0;
        
        const string FOOTER = "sr.exchange.footer";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, -75f, 150f, -45f), FOOTER);
        UI.Text(container, FOOTER, user.Translate("UI.Exchange.Balances", rpBalance, economicsBalance), Anchor.FullStretch, Offset.zero, size: 12, align: TextAnchor.MiddleCenter);
        
        user.SendUI(container);
    }
    
    private double ConvertRPToEconomics(int rp) => rp <= 0 ? 0 : Math.Round((rp * Configuration.Options.ExchangeRate), 2);
    private int ConvertEconomicsToRP(double economics) => economics <= 0 ? 0 : (int)Math.Round((economics / Configuration.Options.ExchangeRate));
    
    #endregion
    
    #region Sell Items
    
    private void CreateSellItems(CuiElementContainer container, UIUser user)
    {
        const string ITEMS = "sr.sellitems";
        UI.Panel(container, UI_MENU, Colors.Clear, Anchor.FullStretch, new Offset(5f, 40f, -5f, -40f), ITEMS);
        
        List<Item> items = Pool.Get<List<Item>>();
        user.Player.inventory.GetAllItems(items);
        FilterList(items, user, _itemContainsFilter);
        
        items.Sort((a, b) =>
        {
            ItemDefinition aDefinition = a.info;
            ItemDefinition bDefinition = b.info;
            
            if (!aDefinition && !bDefinition)
                return 0;
            
            if (!aDefinition)
                return 1;
            
            if (!bDefinition)
                return -1;
            
            return aDefinition.displayName.english.CompareTo(bDefinition.displayName.english);
        });
        
        CreateSellItemsNavigation(container, ITEMS, user, items);
        CreateSellItemsLayout(container, ITEMS, user, items);
        
        Pool.FreeUnmanaged(ref items);
    }

    private void CreateSellItemsNavigation(CuiElementContainer container, string parent, UIUser user, List<Item> items)
    {
        const string NAVIGATION = "sr.sellitems.navigation";
        UI.Panel(container, parent, Configuration.UI.PanelPrimary, Anchor.LeftStretch, new Offset(0f, 0f, 140f, 0f), NAVIGATION);

        const float HEIGHT = 20f;
        const float SPACING = 5f;

        List<ItemCategory> categories = Pool.Get<List<ItemCategory>>();
        categories.Add(ItemCategory.All);
        
        foreach (Item item in items)
        {
            if (!categories.Contains(item.info.category))
                categories.Add(item.info.category);
        }
        
        for (int i = 0; i < categories.Count; i++)
        {
            ItemCategory category = categories[i];
           
            string buttonName = $"{NAVIGATION}.{category}";
            string buttonColor = user.ItemCategory == category ? Configuration.UI.ButtonConfirm : Configuration.UI.Button;
            string textColor = user.ItemCategory == category ? Configuration.UI.ButtonConfirmText : Configuration.UI.ButtonText;

            float yMax = -5f - (i * (HEIGHT + SPACING));

            UI.Panel(container, NAVIGATION, buttonColor, Anchor.TopStretch, new Offset(5f, yMax - HEIGHT, -5f, yMax), buttonName);
            UI.Text(container, buttonName, user.Translate($"UI.ItemCategory.{category.ToString()}"), Anchor.FullStretch, Offset.zero, color: textColor);
            UI.Button(container, buttonName, $"{Commands.ItemCategory} {(int)category}", Anchor.FullStretch, Offset.zero);
        }
        
        Pool.FreeUnmanaged(ref categories);
    }

    private void CreateSellItemsLayout(CuiElementContainer container, string parent, UIUser user, List<Item> items)
    {
        const string LAYOUT = "sr.sellitems.layout";

        UI.Panel(container, parent, Configuration.UI.PanelPrimary, Anchor.FullStretch, new Offset(145f, 0f, 0f, 0f), LAYOUT);
        UI.Text(container, LAYOUT, user.Translate("UI.Sell"), Anchor.FullStretch, Offset.zero, color: Colors.BarelyVisible, size: 120);
        
        const string SCROLL = "sr.sellitems.scroll";
        CuiScrollbar scrollbar = UI.Scrollbar(
            Configuration.UI.ScrollbarHandle, Configuration.UI.ScrollbarHighlight, Configuration.UI.ScrollbarPressed, Configuration.UI.Scrollbar);
        
        CuiRectTransformComponent contentRect = UI.ScrollView(container, LAYOUT, Anchor.FullStretch, new Offset(5f, 5f, -5f, -5f), 
            Anchor.FullStretch, Offset.zero, null, scrollbar, SCROLL);
        
        int count = 0;
        
        _itemsGrid ??= new HorizontalGrid(new Vector2(133f, 155.5f), new Vector2(5f, 5f), 8, 630f);
        
        for (int index = 0; index < items.Count; index++)
        {
            Item item = items[index];
            
            ItemDefinition itemDefinition = item.info;
            if (!itemDefinition)
                continue;
            
            if (itemDefinition.category != user.ItemCategory && user.ItemCategory != ItemCategory.All)
                continue;

            if (!IsOwnedOrFreeItem(user.Player, itemDefinition.itemid, item.skin))
                continue;

            // Item container
            string itemName = $"{LAYOUT}.{index}";
            UI.Panel(container, SCROLL, Configuration.UI.PanelSecondary, Anchor.TopLeft, _itemsGrid.Get(count), itemName);
            
            CreateSellItemElement(container, itemName, item, user);

            count++;
        }
        
        _itemsGrid.ResizeToFit(count, contentRect);
    }
    
    private void CreateSellItemElement(CuiElementContainer container, string parent, Item item, UIUser user)
    {
        string iconName = $"{parent}.icon";
        UI.Panel(container, parent, Colors.Clear, Anchor.TopCenter, new Offset(-64f, -130.5f, 64f, -2.5f), iconName);
        
        int itemId = item.blueprintTarget != 0 ? item.blueprintTarget : item.info.itemid;
        UI.Icon(container, iconName, itemId, item.skin, Anchor.FullStretch, new Offset(16f, 8f, -16f, -24f));
        
        string titleName = $"{parent}.title";
        UI.Panel(container, iconName, Colors.BarelyVisibleDark, Anchor.TopStretch, new Offset(0f, -20f, 0f, 0f), titleName);
        UI.Text(container, titleName, item.info.displayName.english, Anchor.FullStretch, Offset.zero, size: 12);

        float condition = 1f;
        if (item.hasCondition)
        {
            const string COLOR1 = "0.321 0.388 0.203 0.5";
            const string COLOR2 = "0.713 0.952 0.290 0.5";
            
            condition = (item.conditionNormalized + item.maxConditionNormalized) * 0.5f;

            UI.Panel(container, iconName, COLOR1, Anchor.BottomLeft, new Offset(12f, 8f, 16f, Mathf.Lerp(8f, 104f, item.maxConditionNormalized)));
            UI.Panel(container, iconName, COLOR2, Anchor.BottomLeft, new Offset(12f, 8f, 16f, Mathf.Lerp(8f, 104f, item.conditionNormalized)));
        }
        
        if (item.blueprintTarget != 0)
            UI.Icon(container, iconName, Products.Item.BlueprintBase.itemid, 0UL, Anchor.BottomLeft, new Offset(2.5f, 2.5f, 26.5f, 26.5f));

        if (item.amount > 1)
            UI.Text(container, iconName, $"x{item.amount}", Anchor.BottomStretch, new Offset(2.5f, 0f, -2.5f, 20f), align: TextAnchor.LowerRight);

        CreateItemSellButton(container, parent, user, item, condition);
    }
    
    private void CreateItemSellButton(CuiElementContainer container, string parent, UIUser user, Item item, float condition)
    {
        float price = 0f;
        bool canSell = !item.isBroken && _sellPricing.Data.TryGetSellPrice(item.info.shortname, item.skin, out price) && price > 0f;
        if (canSell)
            price = (float)Math.Round(price * condition, 2);
        
        string buttonColor = !canSell ? Configuration.UI.ButtonReject : Configuration.UI.ButtonPurchase;
        string textColor = !canSell ? Configuration.UI.ButtonRejectText : Configuration.UI.ButtonPurchaseText;
        string sprite = !canSell ? Sprites.Occupied : Sprites.MapDollar;
        string command = !canSell ? string.Empty : $"{Commands.Sell} {item.uid.Value} 1";
        Offset iconOffset = !canSell ? new Offset(6f, -7f, 20f, 7f) : new Offset(1f, -12f, 25f, 12f);
        
        string buttonName = $"{parent}.button";
        UI.Panel(container, parent, buttonColor, Anchor.BottomStretch, new Offset(2.5f, 2.5f, -2.5f, 22.5f), buttonName);
        UI.Sprite(container, buttonName, sprite, textColor, Anchor.CenterLeft, iconOffset);
        UI.Text(container, buttonName, !canSell ? user.Translate("UI.NotSellable") : user.Translate("UI.SellPrice", price), Anchor.FullStretch, new Offset(11f, 0f, 0f, 0f), size: 12, color: textColor);
        UI.Button(container, buttonName, command, Anchor.FullStretch, Offset.zero);
    }

    private void CreateSellItemConfirmation(UIUser user, Item item, int amount, float price)
    {
        CuiElementContainer container = UI.Container(Layer.Overlay, UI_MENU, Configuration.UI.Background, Anchor.FullStretch, Offset.zero);

        // Header
        const string HEADER = "sr.sellitem.header";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, 45f, 150f, 75f), HEADER);
        UI.Text(container, HEADER, user.Translate("UI.ConfirmSell"), Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), size: 18, align: TextAnchor.MiddleLeft);
        
        // Sell Info
        const string CONTENTS = "sr.sellitem.contents";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, -40f, 150f, 40f), CONTENTS);
        UI.Icon(container, CONTENTS, item.info.itemid, item.skin, Anchor.CenterRight, new Offset(-75f, -35f, -5f, 35f));
        UI.Text(container, CONTENTS, item.info.displayName.english, Anchor.TopStretch, new Offset(5f, -25f, -74f, -5f), align: TextAnchor.MiddleLeft);
        
        // Unit Price
        UI.Text(container, CONTENTS, user.Translate("UI.UnitPrice"), Anchor.TopStretch, new Offset(5f, -50f, -74f, -30f), size: 12, align: TextAnchor.MiddleLeft);
        UI.Panel(container, CONTENTS, Colors.BarelyVisibleDark, Anchor.TopStretch, new Offset(100f, -50f, -80f, -30f));
        UI.Text(container, CONTENTS, user.Translate("UI.Cost", price), Anchor.TopStretch, new Offset(105f, -50f, -85f, -30f), size: 12, align: TextAnchor.MiddleLeft);
        
        // Amount
        UI.Text(container, CONTENTS, user.Translate("UI.Amount"), Anchor.TopStretch, new Offset(5f, -75f, -74f, -55f), size: 12, align: TextAnchor.MiddleLeft);
        UI.Panel(container, CONTENTS, Colors.BarelyVisibleDark, Anchor.TopStretch, new Offset(100f, -75f, -80f, -55f));
        UI.Input(container, CONTENTS, amount.ToString(), $"{Commands.Sell} {item.uid.Value}", Anchor.TopStretch, new Offset(105f, -75f, -85f, -55f), size: 12, align: TextAnchor.MiddleLeft);
        
        // Footer
        const string FOOTER = "sr.sellitem.footer";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, -75f, 150f, -45f), FOOTER);
        
        const string CANCEL = "sr.sellitem.cancel";
        UI.Panel(container, FOOTER, Configuration.UI.ButtonReject, Anchor.CenterLeft, new Offset(5f, -10f, 135f, 10f), CANCEL);
        UI.Text(container, CANCEL, user.Translate("UI.Cancel"), Anchor.FullStretch, Offset.zero, color: Configuration.UI.ButtonRejectText);
        UI.Button(container, CANCEL, Commands.CancelSell, Anchor.FullStretch, Offset.zero);
        
        const string CONFIRM = "sr.sellitem.confirm";
        UI.Panel(container, FOOTER, Configuration.UI.ButtonConfirm, Anchor.CenterRight, new Offset(-160f, -10f, -5f, 10f), CONFIRM);
        UI.Text(container, CONFIRM, user.Translate("UI.ConfirmSale", Mathf.RoundToInt(price * amount)), Anchor.FullStretch, Offset.zero, color: Configuration.UI.ButtonConfirmText);
        UI.Button(container, CONFIRM, $"{Commands.ConfirmSell} {item.uid.Value} {amount}", Anchor.FullStretch, Offset.zero);
        
        user.SendUI(container);
    }
    
    #endregion
    
    #region Add/Edit Product

    private const float FieldHeight = 20f;
    private const float FieldSpacing = 5f;
    
    private void CreateAddOrEditProductMenu(UIUser user)
    {
        Products.Product product = user.AddEditProduct;
        if (product == null)
            return;
        
        int fieldCount = product switch
        {
            Products.Item => 10,
            Products.Kit => 7,
            Products.Command cmd => 7 + cmd.Commands.Count,
            _ => 0,
        };
        
        float halfHeight = (70f + (fieldCount * (FieldHeight + FieldSpacing)) + FieldSpacing) * 0.5f;
        
        CuiElementContainer container = UI.Container(Layer.Overlay, UI_MENU, Configuration.UI.Background, Anchor.FullStretch, Offset.zero);
        
        const string CONTAINER = "sr.addedit.container";
        UI.Panel(container, UI_MENU, Colors.Clear, Anchor.Center, new Offset(-200f, -halfHeight, 200, halfHeight), CONTAINER);
        
        // Header
        const string HEADER = "sr.addedit.header";
        UI.Panel(container, CONTAINER, Configuration.UI.PanelPrimary, Anchor.TopStretch, new Offset(0f, -30f, 0f, 0f), HEADER);
        UI.Text(container, HEADER, user.Translate(product.ID >= 0 ? "UI.EditProduct" : "UI.CreateProduct"), Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), size: 18, align: TextAnchor.MiddleLeft);
        
        // Fields
        const string FIELDS = "sr.addedit.fields";
        UI.Panel(container, CONTAINER, Configuration.UI.PanelPrimary, Anchor.FullStretch, new Offset(0f, 35f, 0f, -35f), FIELDS);

        switch (product)
        {
            case Products.Item:
                AddOrEditItemFields(container, FIELDS, user);
                break;
            case Products.Kit:
                AddOrEditKitFields(container, FIELDS, user);
                break;
            case Products.Command:
                AddOrEditCommandFields(container, FIELDS, user);
                break;
        } 
        
        // Footer
        const string FOOTER = "sr.addedit.footer";
        UI.Panel(container, CONTAINER, Configuration.UI.PanelPrimary, Anchor.BottomStretch, new Offset(0f, 0f, 0f, 30f), FOOTER);
        
        const string CANCEL = "sr.addedit.cancel";
        UI.Panel(container, FOOTER, Configuration.UI.ButtonReject, Anchor.CenterLeft, new Offset(5f, -10f, 125f, 10f), CANCEL);
        UI.Text(container, CANCEL, user.Translate("UI.Cancel"), Anchor.FullStretch, Offset.zero, color: Configuration.UI.ButtonRejectText);
        UI.Button(container, CANCEL, Commands.CancelProduct, Anchor.FullStretch, Offset.zero);

        bool hasRequiredFields = product.HasRequiredFields();
        string buttonColor = hasRequiredFields ? Configuration.UI.ButtonConfirm : Configuration.UI.ButtonDisabled;
        string textColor = hasRequiredFields ? Configuration.UI.ButtonConfirmText : Configuration.UI.ButtonDisabledText;
        
        const string CONFIRM = "sr.addedit.confirm";
        UI.Panel(container, FOOTER, buttonColor, Anchor.CenterRight, new Offset(-125f, -10f, -5f, 10f), CONFIRM);
        UI.Text(container, CONFIRM, user.Translate("UI.Confirm"), Anchor.FullStretch, Offset.zero, color: textColor);
        UI.Button(container, CONFIRM, hasRequiredFields ? Commands.SaveProduct : string.Empty, Anchor.FullStretch, Offset.zero);
        
        user.SendUI(container);
    }

    private void AddOrEditItemFields(CuiElementContainer container, string parent, UIUser user)
    {
        Products.Item item = user.AddEditProduct as Products.Item;
        if (item == null)
            return;
        
        CreateSelectorField(container, parent, 0, user.Translate("UI.Fields.Item"), item.ItemDefinition.displayName.english, $"{Commands.OpenSelector} {(int)ReturnType.SelectItem}");
        CreateInputField(container, parent, 1, user.Translate("UI.Fields.Amount"), item.Amount.ToString(), $"{Commands.SetField} {nameof(Products.Item.Amount)}");
        CreateInputField(container, parent, 2, user.Translate("UI.Fields.Skin"), item.SkinId.ToString(), $"{Commands.SetField} {nameof(Products.Item.SkinId)}");
        CreateToggleField(container, parent, user, 3, user.Translate("UI.Fields.Blueprint"), item.IsBp, $"{Commands.SetField} {nameof(Products.Item.IsBp)}");
        CreateInputField(container, parent, 4, user.Translate("UI.Fields.DisplayName"), item.DisplayName, $"{Commands.SetField} {nameof(Products.Product.DisplayName)}");
        CreateInputField(container, parent, 5, user.Translate("UI.Fields.Cost"), item.Cost.ToString(), $"{Commands.SetField} {nameof(Products.Product.Cost)}");
        CreateInputField(container, parent, 6, user.Translate("UI.Fields.Cooldown"), item.Cooldown.ToString(), $"{Commands.SetField} {nameof(Products.Product.Cooldown)}");
        CreateInputField(container, parent, 7, user.Translate("UI.Fields.IconUrl"), item.IconURL, $"{Commands.SetField} {nameof(Products.Product.IconURL)}");
        CreateInputField(container, parent, 8, user.Translate("UI.Fields.Permission"), item.Permission, $"{Commands.SetField} {nameof(Products.Product.Permission)}");
        CreateToggleField(container, parent, user, 9, user.Translate("UI.Fields.IgnoreDlcCheck"), item.IgnoreDlcCheck, $"{Commands.SetField} {nameof(Products.Item.IgnoreDlcCheck)}");
    }
    
    private void AddOrEditKitFields(CuiElementContainer container, string parent, UIUser user)
    {
        Products.Kit kit = user.AddEditProduct as Products.Kit;
        if (kit == null)
            return;
        
        CreateSelectorField(container, parent, 0, "KIT", kit.KitName, $"{Commands.OpenSelector} {(int)ReturnType.SelectKit}", true);
        CreateInputField(container, parent, 1, user.Translate("UI.Fields.Description"), kit.Description, $"{Commands.SetField} {nameof(Products.Kit.Description)}");
        CreateInputField(container, parent, 2, user.Translate("UI.Fields.DisplayName"), kit.DisplayName, $"{Commands.SetField} {nameof(Products.Product.DisplayName)}");
        CreateInputField(container, parent, 3, user.Translate("UI.Fields.Cost"), kit.Cost.ToString(), $"{Commands.SetField} {nameof(Products.Product.Cost)}");
        CreateInputField(container, parent, 4, user.Translate("UI.Fields.Cooldown"), kit.Cooldown.ToString(), $"{Commands.SetField} {nameof(Products.Product.Cooldown)}");
        CreateInputField(container, parent, 5, user.Translate("UI.Fields.IconUrl"), kit.IconURL, $"{Commands.SetField} {nameof(Products.Product.IconURL)}");
        CreateInputField(container, parent, 6, user.Translate("UI.Fields.Permission"), kit.Permission, $"{Commands.SetField} {nameof(Products.Product.Permission)}");

    }
    
    private void AddOrEditCommandFields(CuiElementContainer container, string parent, UIUser user)
    {
        Products.Command command = user.AddEditProduct as Products.Command;
        if (command == null)
            return;
        
        CreateInputField(container, parent, 0, user.Translate("UI.Fields.DisplayName"), command.DisplayName, $"{Commands.SetField} {nameof(Products.Product.DisplayName)}", true);
        CreateInputField(container, parent, 1, user.Translate("UI.Fields.Description"), command.Description, $"{Commands.SetField} {nameof(Products.Command.Description)}");
        CreateInputField(container, parent, 2, user.Translate("UI.Fields.Cost"), command.Cost.ToString(), $"{Commands.SetField} {nameof(Products.Product.Cost)}");
        CreateInputField(container, parent, 3, user.Translate("UI.Fields.Cooldown"), command.Cooldown.ToString(), $"{Commands.SetField} {nameof(Products.Product.Cooldown)}");
        CreateInputField(container, parent, 4, user.Translate("UI.Fields.IconUrl"), command.IconURL, $"{Commands.SetField} {nameof(Products.Product.IconURL)}");
        CreateInputField(container, parent, 5, user.Translate("UI.Fields.Permission"), command.Permission, $"{Commands.SetField} {nameof(Products.Product.Permission)}");

        
        int count = command.Commands.Count;
        for (int i = 0; i < count; i++)
        {
            string cmd = command.Commands[i];
            CreateRemovableCommandField(container, parent, 6 + i, i, user.Translate("UI.Fields.Command", i + 1), cmd, i == 0);
        }
        
        CreateInputField(container, parent, 6 + count, user.Translate("UI.Fields.Command", count + 1), "", $"{Commands.SetCommand} {(int)SetCommandType.Add} {count}", count == 0);
    }
    
    #region Selectors
    
    private void CreateItemSelector(UIUser user)
    {
        List<ItemDefinition> items = Pool.Get<List<ItemDefinition>>();
        FilterList(_sortedItemDefinitions, items, user, _itemDefinitionsContainsFilter);
        CreateSelector(user, "UI.SelectItem", ReturnType.SelectItem, items, DrawItemSelector);
        Pool.FreeUnmanaged(ref items);
    }
    
    private void CreateKitSelector(UIUser user)
    {
        List<string> kitNames = Pool.Get<List<string>>();
        Kits?.Call("GetKitNames", kitNames);
        FilterList(kitNames, user, (phrase, kit) => kit.Contains(phrase, CompareOptions.OrdinalIgnoreCase));
        CreateSelector(user, "UI.SelectKit", ReturnType.SelectKit, kitNames, DrawKitSelector);
        Pool.FreeUnmanaged(ref kitNames);
    }
    
    private bool DrawItemSelector(CuiElementContainer container, string parent, UIUser user, float height, float spacing, int count, ItemDefinition itemDefinition)
    {
        bool isSelectedItem = user.AddEditProduct is Products.Item product && product.Shortname == itemDefinition.shortname;
            
        string buttonColor = isSelectedItem ? Configuration.UI.ButtonConfirm : Configuration.UI.Button;
        string textColor = isSelectedItem ? Configuration.UI.ButtonConfirmText : Configuration.UI.ButtonText;
            
        float yMax = -(count * (height + spacing));
            
        string panelName = $"{itemDefinition.itemid}";
        UI.Panel(container, parent, buttonColor, Anchor.TopStretch, new Offset(0f, yMax - height, -5f, yMax), panelName);
        UI.Text(container, panelName, itemDefinition.displayName.english, Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), color: textColor, align: TextAnchor.MiddleLeft);
        UI.Button(container, panelName, $"{Commands.SetField} {nameof(Products.Item.Shortname)} {itemDefinition.shortname}", Anchor.FullStretch, Offset.zero);
        return true;
    }
    
    private bool DrawKitSelector(CuiElementContainer container, string parent, UIUser user, float height, float spacing, int count, string kitName)
    {
        bool isSelectedItem = user.AddEditProduct is Products.Kit product && product.KitName == kitName;
            
        string buttonColor = isSelectedItem ? Configuration.UI.ButtonConfirm : Configuration.UI.Button;
        string textColor = isSelectedItem ? Configuration.UI.ButtonConfirmText : Configuration.UI.ButtonText;
            
        float yMax = -(count * (height + spacing));
            
        string panelName = kitName;
        UI.Panel(container, parent, buttonColor, Anchor.TopStretch, new Offset(0f, yMax - height, -5f, yMax), panelName);
        UI.Text(container, panelName, kitName, Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), color: textColor, align: TextAnchor.MiddleLeft);
        UI.Button(container, panelName, $"{Commands.SetField} {nameof(Products.Kit.KitName)} {kitName}", Anchor.FullStretch, Offset.zero);
            
        return true;
    }
    
    #endregion
    
    #region Generic Fields
    
    private void CreateSelectorField(CuiElementContainer container, string parent, int idx, string label, string value, string onClick, bool required = false)
    {
        float yMax = -FieldSpacing - (idx * (FieldHeight + FieldSpacing));
        
        string fieldName = $"{parent}.{idx}";
        UI.Panel(container, parent, Colors.Clear, Anchor.TopStretch, new Offset(5f, yMax - FieldHeight, -5f, yMax), fieldName);
        
        // Required Icon
        if (required)
            UI.Sprite(container, fieldName, Sprites.Warning, Configuration.UI.ButtonReject, Anchor.CenterLeft, new Offset(4f, -6f, 16f, 6f));
        
        // Label
        UI.Text(container, fieldName, label, new Anchor(0f, 0f, 0.3f, 1f), new Offset(20f, 0f, -2.5f, 0f), size: 12, align: TextAnchor.MiddleLeft);
        
        // Value
        string button = $"{fieldName}.button";
        UI.Panel(container, fieldName, Configuration.UI.Button, new Anchor(0.3f, 0f, 1f, 1f), new Offset(2.5f, 0f, 0f, 0f), button);
        
        if (!string.IsNullOrEmpty(value))
            UI.Text(container, button, value, Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), color: Configuration.UI.ButtonText, size: 12, align: TextAnchor.MiddleLeft);
        
        UI.Button(container, button, onClick, Anchor.FullStretch, Offset.zero);
    }

    private void CreateInputField(CuiElementContainer container, string parent, int idx, string label, string value, string command, bool required = false)
    {
        float yMax = -FieldSpacing - (idx * (FieldHeight + FieldSpacing));
        
        string fieldName = $"{parent}.{idx}";
        UI.Panel(container, parent, Colors.Clear, Anchor.TopStretch, new Offset(5f, yMax - FieldHeight, -5f, yMax), fieldName);
        
        // Required Icon
        if (required)
            UI.Sprite(container, fieldName, Sprites.Warning, Configuration.UI.ButtonReject, Anchor.CenterLeft, new Offset(4f, -6f, 16f, 6f));
        
        // Label
        UI.Text(container, fieldName, label, new Anchor(0f, 0f, 0.3f, 1f), new Offset(20f, 0f, -2.5f, 0f), size: 12, align: TextAnchor.MiddleLeft);
        
        // Value
        string button = $"{fieldName}.button";
        UI.Panel(container, fieldName, Configuration.UI.Button, new Anchor(0.3f, 0f, 1f, 1f), new Offset(2.5f, 0f, 0f, 0f), button);
        UI.Input(container, button, string.IsNullOrEmpty(value) ? string.Empty : value, command, Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), size: 12);
    }
    
    private void CreateRemovableCommandField(CuiElementContainer container, string parent, int idx, int cmdIdx, string label, string value, bool required = false)
    {
        float yMax = -FieldSpacing - (idx * (FieldHeight + FieldSpacing));
        
        string fieldName = $"{parent}.{idx}";
        UI.Panel(container, parent, Colors.Clear, Anchor.TopStretch, new Offset(5f, yMax - FieldHeight, -5f, yMax), fieldName);
        
        // Required Icon
        if (required)
            UI.Sprite(container, fieldName, Sprites.Warning, Configuration.UI.ButtonReject, Anchor.CenterLeft, new Offset(4f, -6f, 16f, 6f));
        
        // Label
        UI.Text(container, fieldName, label, new Anchor(0f, 0f, 0.3f, 1f), new Offset(20f, 0f, -2.5f, 0f), size: 12, align: TextAnchor.MiddleLeft);
        
        // Value
        string button = $"{fieldName}.button";
        UI.Panel(container, fieldName, Configuration.UI.Button, new Anchor(0.3f, 0f, 1f, 1f), new Offset(2.5f, 0f, -25f, 0f), button);
        UI.Input(container, button, string.IsNullOrEmpty(value) ? string.Empty : value, $"{Commands.SetCommand} {(int)SetCommandType.Edit} {cmdIdx}", Anchor.FullStretch, new Offset(5f, 0f, -30f, 0f), size: 12);
        
        // Remove Button
        string removeButton = $"{fieldName}.remove";
        UI.Panel(container, fieldName, Configuration.UI.ButtonReject, Anchor.CenterRight, new Offset(-20f, -10f, 0f, 10f), removeButton);
        UI.Sprite(container, removeButton, Sprites.Close, Configuration.UI.ButtonRejectText, Anchor.Center, new Offset(-7f, -7f, 7f, 7f));
        UI.Button(container, removeButton, $"{Commands.SetCommand} {(int)SetCommandType.Remove} {cmdIdx}", Anchor.FullStretch, Offset.zero);
    }
    
    private void CreateToggleField(CuiElementContainer container, string parent, UIUser user, int idx, string label, bool value, string command, bool required = false)
    {
        float yMax = -FieldSpacing - (idx * (FieldHeight + FieldSpacing));
        
        string fieldName = $"{parent}.{idx}";
        UI.Panel(container, parent, Colors.Clear, Anchor.TopStretch, new Offset(5f, yMax - FieldHeight, -5f, yMax), fieldName);
        
        // Required Icon
        if (required)
            UI.Sprite(container, fieldName, Sprites.Warning, Configuration.UI.ButtonReject, Anchor.CenterLeft, new Offset(4f, -6f, 16f, 6f));
        
        // Label
        UI.Text(container, fieldName, label, new Anchor(0f, 0f, 0.3f, 1f), new Offset(20f, 0f, -2.5f, 0f), size: 12, align: TextAnchor.MiddleLeft);
        
        // Value
        string toggle = $"{fieldName}.toggle";
        
        string buttonColor = value ? Configuration.UI.ButtonConfirm : Configuration.UI.Button;
        string textColor = value ? Configuration.UI.ButtonConfirmText : Configuration.UI.ButtonText;
        
        UI.Panel(container, fieldName, buttonColor, new Anchor(0.3f, 0f, 1f, 1f), new Offset(2.5f, 0f, 0f, 0f), toggle);
        UI.Text(container, toggle, user.Translate(value ? "UI.True" : "UI.False"), Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), color: textColor, size: 12, align: TextAnchor.MiddleLeft);
        UI.Button(container, toggle, $"{command} {!value}", Anchor.FullStretch, Offset.zero);
    }
    
    #endregion
    
    #endregion
    
    #region Delete Product

    private void CreateConfirmDeleteMenu(UIUser user, ProductType productType, int productId)
    {
        CuiElementContainer container = UI.Container(Layer.Overlay, UI_MENU, Configuration.UI.Background, Anchor.FullStretch, Offset.zero);

        const string HEADER = "sr.delete.header";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, 2.5f, 150f, 32.5f), HEADER);
        UI.Text(container, HEADER, user.Translate("UI.ConfirmDelete"), Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), size: 18, align: TextAnchor.MiddleLeft);
        
        const string FOOTER = "sr.delete.footer";
        UI.Panel(container, UI_MENU, Configuration.UI.PanelPrimary, Anchor.Center, new Offset(-150f, -32.5f, 150f, -2.5f), FOOTER);
        
        const string CANCEL = "sr.delete.cancel";
        UI.Panel(container, FOOTER, Configuration.UI.ButtonConfirm, Anchor.CenterLeft, new Offset(5f, -10f, 125f, 10f), CANCEL);
        UI.Text(container, CANCEL, user.Translate("UI.Cancel"), Anchor.FullStretch, Offset.zero, color: Configuration.UI.ButtonConfirmText);
        UI.Button(container, CANCEL, Commands.ReturnToStore, Anchor.FullStretch, Offset.zero);
        
        const string CONFIRM = "sr.delete.confirm";
        UI.Panel(container, FOOTER, Configuration.UI.ButtonReject, Anchor.CenterRight, new Offset(-125f, -10f, -5f, 10f), CONFIRM);
        UI.Text(container, CONFIRM, user.Translate("UI.Confirm"), Anchor.FullStretch, Offset.zero, color: Configuration.UI.ButtonRejectText);
        UI.Sprite(container, CONFIRM, Sprites.Warning, Configuration.UI.ButtonRejectText, Anchor.CenterLeft, new Offset(4f, -8f, 20f, 8f));
        UI.Button(container, CONFIRM, $"{Commands.ConfirmDelete} {(int)productType} {productId}", Anchor.FullStretch, Offset.zero);
        
        user.SendUI(container);
    }
    
    #endregion
    
    #region Toast

    private enum ToastType { Info, Error }
    
    private void CreateToast(UIUser user, ToastType type, string title, string message, float duration = 10f)
    {
        string sprite = type switch
        {
            ToastType.Info => Sprites.Info,
            ToastType.Error => Sprites.Warning,
            _ => string.Empty
        };
        
        string backgroundColor = type switch
        {
            ToastType.Info => Configuration.UI.Toast,
            ToastType.Error => Configuration.UI.ToastError,
            _ => Colors.Clear
        };
        
        string textColor = type switch
        {
            ToastType.Info => Configuration.UI.ToastText,
            ToastType.Error => Configuration.UI.ToastTextError,
            _ => Colors.White
        };
        
        CuiElementContainer container = UI.Container(Layer.Overlay, UI_TOAST, backgroundColor, Anchor.FullStretch, new Offset(450, 50, -450, -620), material: Materials.GreyOut, cursorAndKeyboard: false);
        UI.Sprite(container, UI_TOAST, sprite, textColor, Anchor.TopLeft, new Offset(5f, -25f, 25f, -5f));
        
        UI.Text(container, UI_TOAST, title, Anchor.TopStretch, new Offset(30f, -25f, -5f, -5f), color: textColor, align: TextAnchor.MiddleLeft);
        UI.Text(container, UI_TOAST, message, Anchor.TopStretch, new Offset(30f, -47.5f, -5f, -27.5f), color: textColor, size: 12, align: TextAnchor.MiddleLeft);

        UI.Sprite(container, UI_TOAST, Sprites.Close, textColor, Anchor.TopRight, new Offset(-17f, -17f, -5f, -5f));
        UI.Button(container, UI_TOAST, Commands.CloseToast, Anchor.TopRight, new Offset(-17f, -17f, -5f, -5f));
        
        int idx = user.SendToast(container);
        
        timer.In(duration, () =>
        {
            if (!user?.Player || user.CurrentToastIdx != idx)
                return;
            
            user.CloseToast();
        });
    }
    
    #endregion
    
    #region Common

    private HorizontalGrid _itemsGrid;
    private HorizontalGrid _kitsGrid;
    private HorizontalGrid _kitsWearGrid;
    private HorizontalGrid _kitsBeltGrid;
    private HorizontalGrid _kitsMainGrid;
    private HorizontalGrid _commandsGrid;
    
    private void CreateSelector<T>(UIUser user, string title, ReturnType returnType, List<T> list, Func<CuiElementContainer, string, UIUser, float, float, int, T, bool> draw)
    {
        CuiElementContainer container = UI.Container(Layer.Overlay, UI_MENU, Configuration.UI.Background, Anchor.FullStretch, Offset.zero);
        
        const string CONTAINER = "sr.selector.container";
        UI.Panel(container, UI_MENU, Colors.Clear, Anchor.Center, new Offset(-150f, -300, 150, 300), CONTAINER);
        
        // Header
        const string HEADER = "sr.selector.header";
        UI.Panel(container, CONTAINER, Configuration.UI.PanelPrimary, Anchor.TopStretch, new Offset(0f, -30f, 0f, 0f), HEADER);
        UI.Text(container, HEADER, user.Translate(title), Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f), size: 18, align: TextAnchor.MiddleLeft);
        
        const string BUTTON_CLOSE = "sr.selector.close";
        UI.Panel(container, HEADER, Configuration.UI.ButtonReject, Anchor.CenterRight, new Offset(-25f, -10f, -5f, 10f), BUTTON_CLOSE);
        UI.Sprite(container, BUTTON_CLOSE, Sprites.Close, Configuration.UI.ButtonRejectText, Anchor.Center, new Offset(-7f, -7f, 7f, 7f));
        UI.Button(container, BUTTON_CLOSE, $"{Commands.CloseSelector} {(int)returnType}", Anchor.FullStretch, Offset.zero);
        
        // Search Bar
        const string SEARCH = "sr.selector.search";
        UI.Panel(container, CONTAINER, Configuration.UI.PanelPrimary, Anchor.TopStretch, new Offset(0f, -65f, 0f, -35f), SEARCH);
        if (!string.IsNullOrEmpty(_magnifyIcon))
            UI.PNG(container, SEARCH, _magnifyIcon, Anchor.LeftStretch, new Offset(5f, 5f, 25f, -5f));

        const string UI_INPUT = "sr.selector.search.input";
        UI.Panel(container, SEARCH, Configuration.UI.PanelSecondary, Anchor.FullStretch, new Offset(30f, 5f, -5f, -5f), UI_INPUT);
        UI.Input(container, UI_INPUT, user.SearchFilter, $"{Commands.Search} {(int)returnType}", Anchor.FullStretch, new Offset(5f, 0f, -5f, 0f));
        
        // Layout
        const string LAYOUT = "sr.selector.layout";
        UI.Panel(container, CONTAINER, Configuration.UI.PanelPrimary, Anchor.FullStretch, new Offset(0f, 0f, 0f, -70f), LAYOUT);
        
        const string SCROLL = "sr.selector.scroll";
        CuiScrollbar scrollbar = UI.Scrollbar(
            Configuration.UI.ScrollbarHandle, Configuration.UI.ScrollbarHighlight, Configuration.UI.ScrollbarPressed, Configuration.UI.Scrollbar);
        
        CuiRectTransformComponent contentRect = UI.ScrollView(container, LAYOUT, Anchor.FullStretch, new Offset(5f, 5f, -5f, -5f), 
            Anchor.FullStretch, Offset.zero, null, scrollbar, SCROLL);

        const float VIEWPORT = 560f;
        const float HEIGHT = 20f;
        const float SPACING = 5f;
        
        int count = 0;
        
        for (int i = 0; i < list.Count; i++)
        {
            if (draw(container, SCROLL, user, HEIGHT, SPACING, count, list[i]))
                count++;
        }
        
        float height = Mathf.Max(VIEWPORT, (count * HEIGHT) + (Mathf.Max(count - 1, 0) * SPACING));
        
        contentRect.OffsetMin = $"0 {-(height - VIEWPORT)}";
        contentRect.OffsetMax = "0 0";
        
        user.SendUI(container);
    }
    
    private void CreatePurchaseButton(CuiElementContainer container, string parent, UIUser user, ProductType productType, int productId, int productCost, int balance, bool isDlcItem = false)
    {
        bool isOnCooldown = _cooldowns.Data.HasCooldown(user.UserId, productId, out double remaining);
        
        bool canPurchase = productCost <= balance && !isOnCooldown;
        
        string buttonColor = !canPurchase || isDlcItem ? Configuration.UI.ButtonReject : Configuration.UI.ButtonPurchase;
        string textColor = !canPurchase || isDlcItem ? Configuration.UI.ButtonRejectText : Configuration.UI.ButtonPurchaseText;

        string buttonText = isDlcItem ? user.Translate("UI.DLCItem") : productCost == 0 ? user.Translate("UI.Free") : user.Translate("UI.Cost", productCost);
        
        string sprite = !canPurchase || isDlcItem ? Sprites.Occupied : Sprites.Cart;
        string command = !canPurchase || isDlcItem ? string.Empty : $"{Commands.Purchase} {(int)productType} {productId}";
        
        Offset iconOffset = !canPurchase || isDlcItem ? new Offset(6f, -7f, 20f, 7f) : new Offset(4f, -9f, 22f, 9f);
        
        string buttonName = $"{parent}.button";
        
        UI.Panel(container, parent, buttonColor, Anchor.BottomStretch, new Offset(2.5f, 2.5f, -2.5f, 22.5f), buttonName);
        UI.Sprite(container, buttonName, sprite, textColor, Anchor.CenterLeft, iconOffset);
        
        if (isOnCooldown)
            UI.Countdown(container, buttonName, "%TIME_LEFT%", (int)remaining, 0, Anchor.FullStretch, new Offset(11f, 0f, 0f, 0f), color: textColor);
        else UI.Text(container, buttonName, buttonText, Anchor.FullStretch, new Offset(11f, 0f, 0f, 0f), size: 12, color: textColor);
        
        UI.Button(container, buttonName, command, Anchor.FullStretch, Offset.zero);
    }

    private void CreateAdminButtons(CuiElementContainer container, string parent, UIUser user, ProductType productType, int productId)
    {
        string editButtonName = $"{parent}.button.edit";
        UI.Panel(container, parent, Configuration.UI.ButtonPurchase, new Anchor(0f, 0f, 0.5f, 0f), new Offset(2.5f, 2.5f, -2.5f, 22.5f), editButtonName);
        UI.Sprite(container, editButtonName, Sprites.LevelMetal, Configuration.UI.ButtonPurchaseText, Anchor.CenterLeft, new Offset(7f, -6f, 19f, 6f));
        UI.Text(container, editButtonName, user.Translate("UI.Edit"), Anchor.FullStretch, new Offset(11f, 0f, 0f, 0f), color: Configuration.UI.ButtonPurchaseText, size: 10);
        UI.Button(container, editButtonName, $"{Commands.EditProduct} {(int)productType} {productId}", Anchor.FullStretch, Offset.zero);
        
        string deleteButtonName = $"{parent}.button.delete";
        UI.Panel(container, parent, Configuration.UI.ButtonReject, new Anchor(0.5f, 0f, 1f, 0f), new Offset(2.5f, 2.5f, -2.5f, 22.5f), deleteButtonName);
        UI.Sprite(container, deleteButtonName, Sprites.Close, Configuration.UI.ButtonRejectText, Anchor.CenterLeft, new Offset(7f, -6f, 19f, 6f));
        UI.Text(container, deleteButtonName, user.Translate("UI.Delete"), Anchor.FullStretch, new Offset(11f, 0f, 0f, 0f), color: Configuration.UI.ButtonRejectText, size: 10);
        UI.Button(container, deleteButtonName, $"{Commands.DeleteProduct} {(int)productType} {productId}", Anchor.FullStretch, Offset.zero);
    }
    
    #endregion
    
    #region Filters
    
    private Func<string, Item, bool> _itemContainsFilter;
    private Func<string, ItemDefinition, bool> _itemDefinitionsContainsFilter;
    private Func<string, Products.Item, bool> _itemProductsContainsFilter;
    private Func<string, Products.Kit, bool> _kitProductsContainsFilter;
    private Func<string, Products.Command, bool> _commandProductsContainsFilter;

    private void CreateCommonFilters()
    {
        _itemContainsFilter = ItemContainsValidator;
        _itemDefinitionsContainsFilter = ItemDefinitionsContainsValidator;
        _itemProductsContainsFilter = ItemProductContainsValidator;
        _kitProductsContainsFilter = KitProductContainsValidation;
        _commandProductsContainsFilter = CommandProductContainsValidation;
    }
    
    private void FilterList<T>(List<T> src, List<T> dst, UIUser uiUser, Func<string, T, bool> contains)
    {
        if (string.IsNullOrEmpty(uiUser.SearchFilter))
            dst.AddRange(src);
        else
        {
            for (int i = 0; i < src.Count; i++)
            {
                T t = src[i];
                if (contains(uiUser.SearchFilter, t))
                    dst.Add(t);
            }
        }
    }
    
    private void FilterList<T>(List<T> src, UIUser uiUser, Func<string, T, bool> contains)
    {
        if (string.IsNullOrEmpty(uiUser.SearchFilter))
            return;
        
        for (int i = src.Count - 1; i >= 0; i--)
        {
            T t = src[i];
            if (!contains(uiUser.SearchFilter, t))
                src.RemoveAt(i);
        }
    }
    
    private bool ItemContainsValidator(string phrase, Item item) => 
        item.info.displayName.english.Contains(phrase, CompareOptions.OrdinalIgnoreCase) ||
        item.info.shortname.Contains(phrase, CompareOptions.OrdinalIgnoreCase);
    
    private bool ItemDefinitionsContainsValidator(string phrase, ItemDefinition itemDefinition) => 
        itemDefinition.displayName.english.Contains(phrase, CompareOptions.OrdinalIgnoreCase) ||
        itemDefinition.shortname.Contains(phrase, CompareOptions.OrdinalIgnoreCase);

    private bool ItemProductContainsValidator(string phrase, Products.Item item) => 
        (item.ItemDefinition && item.ItemDefinition.displayName.english.Contains(phrase, CompareOptions.OrdinalIgnoreCase)) ||
        item.Shortname.Contains(phrase) ||
        (!string.IsNullOrEmpty(item.DisplayName) && item.DisplayName.Contains(phrase, CompareOptions.OrdinalIgnoreCase));

    private bool KitProductContainsValidation(string phrase, Products.Kit kit) => 
        kit.DisplayName.Contains(phrase, CompareOptions.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(kit.Description) && kit.Description.Contains(phrase, CompareOptions.OrdinalIgnoreCase));
    
    private bool CommandProductContainsValidation(string phrase, Products.Command command) => 
        command.DisplayName.Contains(phrase, CompareOptions.OrdinalIgnoreCase);
    
    #endregion

    #region Player

    private class UIUser
    {
        public BasePlayer Player;
        public ulong UserId;
        
        public List<NavigationCategory> AvailableCategories;
        public NavigationCategory Category = NavigationCategory.None;
        public ItemCategory ItemCategory = ItemCategory.All;
        public string SearchFilter = string.Empty;
        public bool AdminMode = false;
        public bool HasToast = false;
        public bool ExitToGame = false;
        
        public NpcStore NpcStore;
        public Products.Product AddEditProduct;

        public BasePlayer TransferTarget;
        public int TransferAmount = 1;

        private int _toastIdx = 0;
        public int CurrentToastIdx => _toastIdx;

        public UIUser(BasePlayer player)
        {
            Player = player;
            UserId = player.userID;
            AvailableCategories = Pool.Get<List<NavigationCategory>>();
        }

        public void UpdateAvailableCategories(StoreNavigation navigation, Products products, bool economics)
        {
            AvailableCategories.Clear();
            
            if ((navigation.Items && products.Items.Count > 0) || AdminMode)
                AvailableCategories.Add(NavigationCategory.Items);
            if ((navigation.Kits && products.Kits.Count > 0) || AdminMode)
                AvailableCategories.Add(NavigationCategory.Kits);
            if ((navigation.Commands && products.Commands.Count > 0) || AdminMode)
                AvailableCategories.Add(NavigationCategory.Commands);
            if (navigation.Seller)
                AvailableCategories.Add(NavigationCategory.Sell);
            if (navigation.Transfer)
                AvailableCategories.Add(NavigationCategory.Transfer);
            if (navigation.Exchange && economics)
                AvailableCategories.Add(NavigationCategory.Exchange);
        }

        public void SendUI(CuiElementContainer container)
        {
            CloseToast();
            
            CuiHelper.AddUi(Player, container);
            UIPool.Free(ref container);
        }

        public void CloseMenu()
        {
            Pool.FreeUnmanaged(ref AvailableCategories);
            UIUsers.Remove(UserId);

            CloseToast();
            
            if (Player)
                CuiHelper.DestroyUi(Player, UI_MENU);
        }

        public int SendToast(CuiElementContainer container)
        {
            _toastIdx++;
            HasToast = true;
            
            CuiHelper.AddUi(Player, container);
            UIPool.Free(ref container);
            return _toastIdx;
        }
        
        public void CloseToast()
        {
            if (!HasToast)
                return;
            
            HasToast = false;
            
            if (Player)
                CuiHelper.DestroyUi(Player, UI_TOAST);
        }
        
        public string Translate(string key, params object[] args) => 
            args == null || args.Length == 0 ? _getMessage(key, Player.UserIDString) : string.Format(_getMessage(key, Player.UserIDString), args);

        private static readonly Hash<ulong, UIUser> UIUsers = new Hash<ulong, UIUser>();

        public static UIUser Get(BasePlayer player)
        {
            if (!player)
                return null;
            
            if (!UIUsers.TryGetValue(player.userID, out UIUser user))
                UIUsers[player.userID] = user = new UIUser(player);

            return user;
        }

        public static void OnUnload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, UI_MENU);
                CuiHelper.DestroyUi(player, UI_TOAST);
            }

            UIUsers.Clear();
        }
    }
    
    #endregion

    #region UI Commands

    private static class Commands
    {
        public const string Close = "srui.close";
        public const string AdminToggle = "srui.admintoggle";
        public const string Navigation = "srui.navigation";
        public const string Search = "srui.search";
        public const string ItemCategory = "srui.itemcategory";
        public const string Purchase = "srui.purchase";
        public const string Sell = "srui.sell";
        public const string ConfirmSell = "srui.confirmsell";
        public const string CancelSell = "srui.cancelsell";
        public const string AddProduct = "srui.addproduct";
        public const string EditProduct = "srui.editproduct";
        public const string OpenSelector = "srui.openselector";
        public const string CloseSelector = "srui.closeselector";
        public const string SetField = "srui.addedit.setfield";
        public const string SetCommand = "srui.addedit.setcommand";
        public const string SaveProduct = "srui.saveproduct";
        public const string CancelProduct = "srui.cancelproduct";
        public const string DeleteProduct = "srui.deleteproduct";
        public const string ConfirmDelete = "srui.confirmdelete";
        public const string ReturnToStore = "srui.returntostore";
        public const string CloseToast = "srui.closetoast";
        public const string Transfer = "srui.transfer";
        public const string ConfirmTransfer = "srui.confirmtransfer";
        public const string Exchange = "srui.exchange";
        public const string ConvertToEconomics = "srui.converttoeconomics";
        public const string ConvertToRP = "srui.converttorp";
    }
    
    private enum ReturnType
    {
        Store,
        SelectItem,
        SelectKit,
        SelectTransferTarget,
    }

    #region Admin Commands
    
    [ConsoleCommand(Commands.AdminToggle)]
    private void CommandAdminToggle(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player || !IsAdmin(player))
            return;
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        user.AdminMode = !user.AdminMode;
        user.SearchFilter = string.Empty;
        OpenStore(player);
    }
    
    [ConsoleCommand(Commands.AddProduct)]
    private void CommandAddProduct(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player || !IsAdmin(player))
            return;
        
        UIUser user = UIUser.Get(player);
        if (user is not { AdminMode: true })
            return;
        
        user.AddEditProduct = user.Category switch
        {
            NavigationCategory.Items => new Products.Item{ Shortname = "rifle.ak", DisplayName = "Assault Rifle", Amount = 1, Category = ItemCategory.Weapon },
            NavigationCategory.Kits => new Products.Kit(),
            NavigationCategory.Commands => new Products.Command(),
            _ => null
        };
        user.SearchFilter = string.Empty;
        
        if (user.AddEditProduct == null)
            return;
        
        CreateAddOrEditProductMenu(user);
    }
    
    [ConsoleCommand(Commands.EditProduct)]
    private void CommandEditProduct(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player || !IsAdmin(player))
            return;
        
        UIUser user = UIUser.Get(player);
        if (user is not { AdminMode: true })
            return;
        
        ProductType productType = (ProductType)arg.GetInt(0, 0);
        int productId = arg.GetInt(1, -1);
        
        if (productType == ProductType.None || productId < 0)
            return;

        Products products = user.NpcStore is { CustomStore: true } ? user.NpcStore.Products : _products.Data;
        
        if (!products.TryFindProductByID(productType, productId, out Products.Product product))
            return;
        
        user.AddEditProduct = productType switch
        {
            ProductType.Item => new Products.Item(product),
            ProductType.Kit => new Products.Kit(product),
            ProductType.Command => new Products.Command(product),
        };
        user.SearchFilter = string.Empty;
        
        CreateAddOrEditProductMenu(user);
    }

    [ConsoleCommand(Commands.OpenSelector)]
    private void CommandOpenSelector(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player)
            return;

        UIUser user = UIUser.Get(player);
        if (user == null)
            return;

        ReturnType returnType = (ReturnType)arg.GetInt(0, 0);

        switch (returnType)
        {
            case ReturnType.Store:
                return;
            case ReturnType.SelectItem:
            {
                if (!user.AdminMode)
                    return;
                
                Products.Item item = user.AddEditProduct as Products.Item;
                if (item == null)
                    return;

                CreateItemSelector(user);
                return;
            }
            case ReturnType.SelectKit:
            {
                if (!user.AdminMode)
                    return;
                
                Products.Kit kit = user.AddEditProduct as Products.Kit;
                if (kit == null)
                    return;

                CreateKitSelector(user);
                return;
            }
            case ReturnType.SelectTransferTarget:
            {
                CreatePlayerSelector(user);
                return;
            }
        }
    }
    
    [ConsoleCommand(Commands.CloseSelector)]
    private void CommandCloseSelector(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player || !IsAdmin(player))
            return;

        UIUser user = UIUser.Get(player);
        if (user == null)
            return;

        ReturnType returnType = (ReturnType)arg.GetInt(0, 0);
        
        if (user.AdminMode && (returnType == ReturnType.SelectItem && user.AddEditProduct is Products.Item ||
            returnType == ReturnType.SelectKit && user.AddEditProduct is Products.Kit))
            CreateAddOrEditProductMenu(user);
        if (returnType == ReturnType.SelectTransferTarget)
            CreateTransferMenu(user);
    }
    
    [ConsoleCommand(Commands.SetField)]
    private void CommandAddEditSetField(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player || !IsAdmin(player))
            return;
        
        UIUser user = UIUser.Get(player);
        if (user is not { AdminMode: true })
            return;

        Products.Product product = user.AddEditProduct;
        if (product == null)
            return;

        string fieldName = arg.GetString(0, string.Empty);
        string value = arg.Args.Length >= 2 ? string.Join(" ", arg.Args.Skip(1)) : string.Empty;

        product.SetField(fieldName, value);
        
        CreateAddOrEditProductMenu(user);
    }
    
    private enum SetCommandType { Add, Edit, Remove }
    
    [ConsoleCommand(Commands.SetCommand)]
    private void CommandAddEditSetCommand(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player || !IsAdmin(player))
            return;
        
        UIUser user = UIUser.Get(player);
        if (user is not { AdminMode: true })
            return;

        Products.Command command = user.AddEditProduct as Products.Command;
        if (command == null)
            return;

        SetCommandType commandType = (SetCommandType)arg.GetInt(0, 0);
        int index = arg.GetInt(1, -1);
        string value = arg.Args.Length >= 3 ? string.Join(" ", arg.Args.Skip(2)) : string.Empty;

        command.SetCommand(commandType, index, value);
        
        CreateAddOrEditProductMenu(user);
    }
    
    [ConsoleCommand(Commands.CancelProduct)]
    private void CommandCancelProduct(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player || !IsAdmin(player))
            return;
        
        UIUser user = UIUser.Get(player);
        if (user is not { AdminMode: true })
            return;

        user.AddEditProduct = null;
        user.SearchFilter = string.Empty;
        
        OpenStore(player);
    }
    
    [ConsoleCommand(Commands.SaveProduct)]
    private void CommandSaveProduct(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player || !IsAdmin(player))
            return;
        
        UIUser user = UIUser.Get(player);
        if (user is not { AdminMode: true })
            return;

        Products.Product product = user.AddEditProduct;

        if (product == null)
            return;

        if (!product.HasRequiredFields())
            return;

        Products products = user.NpcStore is { CustomStore: true } ? user.NpcStore.Products : _products.Data;
        
        if (!products.AddOrUpdateProduct(product))
            return;
        
        if (!string.IsNullOrEmpty(product.Permission) && !permission.PermissionExists(product.Permission, this))
            permission.RegisterPermission(product.Permission, this);
        
        if (user.NpcStore is { CustomStore: true })
            _npcStores.Save();
        else _products.Save();

        if (!string.IsNullOrEmpty(product.IconURL))
            AddImage(product.IconURL);

        user.AddEditProduct = null;
        user.SearchFilter = string.Empty;
        
        OpenStore(player);
    }
    
    [ConsoleCommand(Commands.DeleteProduct)]
    private void CommandDeleteProduct(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player || !IsAdmin(player))
            return;
        
        UIUser user = UIUser.Get(player);
        if (user is not { AdminMode: true })
            return;
        
        ProductType productType = (ProductType)arg.GetInt(0, 0);
        int productId = arg.GetInt(1, -1);
        
        if (productType == ProductType.None || productId < 0)
            return;

        CreateConfirmDeleteMenu(user, productType, productId);
    }
    
    [ConsoleCommand(Commands.ConfirmDelete)]
    private void CommandConfirmDeleteProduct(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player || !IsAdmin(player))
            return;
        
        UIUser user = UIUser.Get(player);
        if (user is not { AdminMode: true })
            return;
        
        ProductType productType = (ProductType)arg.GetInt(0, 0);
        int productId = arg.GetInt(1, -1);
        
        if (productType == ProductType.None || productId < 0)
            return;
        
        Products products = user.NpcStore is { CustomStore: true } ? user.NpcStore.Products : _products.Data;
        if (products.DeleteByProductID(productType, productId))
        {
            if (user.NpcStore is { CustomStore: true })
                _npcStores.Save();
            else _products.Save();
        }

        OpenStore(player);
    }
    
    #endregion
    
    [ConsoleCommand(Commands.Close)]
    private void CommandClose(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        user.CloseMenu();
    }
    
    [ConsoleCommand(Commands.CloseToast)]
    private void CommandCloseToast(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        user.CloseToast();
    }
    
    
    [ConsoleCommand(Commands.ReturnToStore)]
    private void CommandReturnToStore(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player)
            return;
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        OpenStore(player);
    }
    
    [ConsoleCommand(Commands.Navigation)]
    private void CommandSelectNavigation(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        NavigationCategory category = (NavigationCategory)arg.GetInt(0, 0);
        if (category == NavigationCategory.Transfer)
        {
            user.TransferTarget = null;
            user.TransferAmount = 0;
            CreateTransferMenu(user);
        }
        else if (category == NavigationCategory.Exchange)
        {
            CreateExchangeMenu(user);
        }
        else
        {
            user.Category = (NavigationCategory)arg.GetInt(0, 0);
            OpenStore(player);
        }
    }
    
    [ConsoleCommand(Commands.Search)]
    private void CommandSearch(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        user.SearchFilter = arg.Args.Length >= 2 ? string.Join(" ", arg.Args.Skip(1)) : string.Empty;

        ReturnType returnTo = (ReturnType)arg.GetInt(0, 0);
        switch (returnTo)
        {
            case ReturnType.SelectItem:
                if (!user.AdminMode)
                    OpenStore(player);
                else CreateItemSelector(user);
                return;
            
            case ReturnType.SelectKit:
                if (!user.AdminMode)
                    OpenStore(player);
                else CreateKitSelector(user);
                return;
            
            case ReturnType.SelectTransferTarget:
                CreatePlayerSelector(user);
                return;
            
            default:
                OpenStore(player);
                return;
        }
    }

    [ConsoleCommand(Commands.ItemCategory)]
    private void CommandSelectItemCategory(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;

        user.ItemCategory = (ItemCategory)arg.GetInt(0, 0);
        OpenStore(player);
    }
    
    [ConsoleCommand(Commands.Purchase)]
    private void CommandPurchase(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;

        ProductType productType = (ProductType)arg.GetInt(0, 0);
        int productId = arg.GetInt(1, -1);
        
        if (productType == ProductType.None || productId < 0)
            return;
        
        Products products = user.NpcStore is { CustomStore: true } ? user.NpcStore.Products : _products.Data;
        if (!products.TryFindProductByID(productType, productId, out Products.Product product))
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Error.InvalidProduct"));
            return;
        }

        if (_cooldowns.Data.HasCooldown(player.userID, productId, out double remaining))
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Error.OnCooldown"));
            return;
        }

        _balances.Data.TryGetValue(player.userID, out int balance);

        if (product.Cost > balance)
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Error.InsufficientBalance"));
            return;
        }
        
        if (!product.GiveToPlayer(player))
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Error.GivePurchase"));
            return;
        }
        
        if (product.Cost > 0)
            _balances.Data[player.userID] = balance - product.Cost;
        
        SendPointsUpdated(player.userID);
        
        if (product.Cooldown > 0)
            _cooldowns.Data.AddCooldown(player.userID, productId, product.Cooldown);
        
        if (Configuration.Options.Logs)
            LogToFile("Purchases", $"{player.displayName} ({player.userID}) bought {product.DisplayName} (ID: {product.ID}) for {product.Cost}RP", this, true, true);
        
        OpenStore(player);
        
        CreateToast(user, ToastType.Info, user.Translate("UI.Purchase.Title"), 
            product.Cost > 0 ? user.Translate("UI.Purchase.Success", product.PurchaseName, product.Cost) : 
                user.Translate("UI.Purchase.Free", product.PurchaseName));
    }
    
    [ConsoleCommand(Commands.Sell)]
    private void CommandSell(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;

        ulong itemId = arg.GetUInt64(0, 0UL);
        if (itemId == 0UL)
            return;
        
        int amount = Mathf.Max(arg.GetInt(1, 1), 1);
        
        Item item = player.inventory.FindItemByUID(new ItemId(itemId));
        if (item == null || item.isBroken)
            return;

        float condition = item.hasCondition ? (item.conditionNormalized + item.maxConditionNormalized) * 0.5f : 1f;
            
        bool canSell = _sellPricing.Data.TryGetSellPrice(item.info.shortname, item.skin, out float price) && price > 0f;
        if (!canSell)
            return;
        
        price = (float)Math.Round(price * condition, 2);
        amount = Mathf.Min(amount, item.amount);
        
        CreateSellItemConfirmation(user, item, amount, price);
    }
    
    [ConsoleCommand(Commands.CancelSell)]
    private void CommandCancelSell(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        OpenStore(player);
    }
    
    [ConsoleCommand(Commands.ConfirmSell)]
    private void CommandConfirmSell(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        ulong itemId = arg.GetUInt64(0, 0UL);
        if (itemId == 0UL)
            return;
        
        int amount = Mathf.Max(arg.GetInt(1, 0), 0);
        
        Item item = player.inventory.FindItemByUID(new ItemId(itemId));
        if (item == null || item.isBroken)
            return;

        float condition = item.hasCondition ? (item.conditionNormalized + item.maxConditionNormalized) * 0.5f : 1f;
            
        bool canSell = _sellPricing.Data.TryGetSellPrice(item.info.shortname, item.skin, out float price) && price > 0f;
        if (!canSell)
            return;
        
        amount = Mathf.Min(amount, item.amount);
        int total = Mathf.RoundToInt((float)Math.Round(price * condition, 2) * amount);
        if (total <= 0)
            return;

        if (amount == item.amount)
        {
            item.Remove(0f);
            item.RemoveFromContainer();
        }
        else
        {
            item.amount -= amount;
            item.MarkDirty();
        }
        
        if (!_balances.Data.ContainsKey(player.userID))
            _balances.Data[player.userID] = total;
        else _balances.Data[player.userID] += total;
        
        SendPointsUpdated(player.userID);

        if (Configuration.Options.Logs)
            LogToFile("Sales", $"{player.displayName} ({player.userID}) sold {amount} x {item.info.shortname} for {total}RP", this, true, true);
        
        OpenStore(player);
        CreateToast(user, ToastType.Info, user.Translate("UI.Sold.Title"), user.Translate("UI.Sold.Message", amount, item.info.displayName.english, total));
    }
    
    [ConsoleCommand(Commands.Transfer)]
    private void CommandTransfer(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        string targetId = arg.GetString(0, string.Empty);
        if (string.IsNullOrEmpty(targetId))
            user.TransferTarget = null;
        else
        {
            if (ulong.TryParse(targetId, out ulong userId))
                user.TransferTarget = BasePlayer.FindAwakeOrSleepingByID(userId);
        }

        if (arg.Args.Length > 1)
        {
            _balances.Data.TryGetValue(player.userID, out int balance);
            user.TransferAmount = Mathf.Clamp(arg.GetInt(1, 0), 0, balance);
        }
        
        CreateTransferMenu(user);
    }
    
    [ConsoleCommand(Commands.ConfirmTransfer)]
    private void CommandConfirmTransfer(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;

        BasePlayer target = user.TransferTarget;
        int amount = user.TransferAmount;
        
        if (amount <= 0)
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Error.ZeroTransferAmount"));
            return;
        }

        if (!target)
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Error.NoTransferTarget"));
            return;
        }

        _balances.Data.TryGetValue(player.userID, out int balance);
        if (balance < amount)
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Error.InsufficientTransferBalance"));
            return;
        }
        
        _balances.Data[player.userID] = balance - amount;
        
        if (!_balances.Data.ContainsKey(target.userID))
            _balances.Data[target.userID] = amount;
        else _balances.Data[target.userID] += amount;
        
        SendPointsUpdated(player.userID);
        SendPointsUpdated(target.userID);
        
        OpenStore(player);
        
        CreateToast(user, ToastType.Info, user.Translate("UI.Transfer.Success"), user.Translate("UI.Transfer.Sent", amount, target.displayName));
        user.TransferTarget.ChatMessage(string.Format(_getMessage("Message.Transfer.Received", target.UserIDString), amount, player.displayName));
        
        if (Configuration.Options.Logs)
            LogToFile("Transfers", $"{player.displayName} ({player.userID}) transferred {amount}RP to {target.displayName} ({target.userID})", this, true, true);
        
        user.TransferTarget = null;
        user.TransferAmount = 0;
    }
    
    [ConsoleCommand(Commands.Exchange)]
    private void CommandExchange(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player)
            return;
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        int rp = arg.GetInt(0, 0);
        int economics = arg.GetInt(1, 0);
        
        ExchangeType type = (ExchangeType)arg.GetInt(2, 0);
        if (type == ExchangeType.None)
            return;

        int inputValue = arg.GetInt(3, 0);
        if (type == ExchangeType.RP)
        {
            _balances.Data.TryGetValue(player.userID, out int balance);
            rp = Mathf.Clamp(inputValue, 0, balance);
        }
        else
        {
            double balance = Economics?.Call<double>("Balance", player.UserIDString) ?? 0;
            economics = Mathf.Clamp(inputValue, 0, (int)balance);
            
            // Round economics to nearest Configuration.Options.ExchangeRate that doesnt exceed the balance
            economics = (int)(Mathf.Floor(economics / Configuration.Options.ExchangeRate) * Configuration.Options.ExchangeRate);
        }
        
        CreateExchangeMenu(user, rp, economics);
    }

    [ConsoleCommand(Commands.ConvertToEconomics)]
    private void CommandConvertToEconomics(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player)
            return;
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        int rp = arg.GetInt(0, 0);
        
        _balances.Data.TryGetValue(player.userID, out int balance);
        if (rp > balance)
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Exchange.Error.Balance.RP"));
            return;
        }
        rp = Mathf.Clamp(rp, 0, balance);

        if (!Economics)
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Exchange.Error.NoEconomics"));
            return;
        }

        double economics = ConvertRPToEconomics(rp);
        if (!Economics.Call<bool>("Deposit", player.UserIDString, economics))
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Exchange.Error.Economics.DepositFailed"));
            return;
        }
        
        _balances.Data[player.userID] -= rp;
        
        LogToFile("Exchange", $"{player.displayName} exchanged {rp} RP for {economics} Economics", this, true, true);
        
        SendPointsUpdated(player.userID);
        
        CreateExchangeMenu(user, 0, 0);
        
        CreateToast(user, ToastType.Info, user.Translate("UI.Exchange.Success"), user.Translate("UI.Exchange.Success.RPToEconomics", rp, (int)economics));
    }
    
    [ConsoleCommand(Commands.ConvertToRP)]
    private void CommandConvertToRP(ConsoleSystem.Arg arg)
    {
        BasePlayer player = arg.Player();
        if (!player)
            return;
        
        UIUser user = UIUser.Get(player);
        if (user == null)
            return;
        
        int economics = arg.GetInt(0, 0);
        
        if (!Economics)
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Exchange.Error.NoEconomics"));
            return;
        }
        
        double balance = Economics.Call<double>("Balance", player.UserIDString);
        if (economics > balance)
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Exchange.Error.Balance.Economics"));
            return;
        }
        
        economics = Mathf.Clamp(economics, 0, (int)balance);
        if (!Economics.Call<bool>("Withdraw", player.UserIDString, (double)economics))
        {
            CreateToast(user, ToastType.Error, user.Translate("UI.Error.Title"), user.Translate("UI.Exchange.Error.Economics.WithdrawFailed"));
            return;
        }
        
        int rp = ConvertEconomicsToRP(economics);
        
        if (!_balances.Data.ContainsKey(player.userID))
            _balances.Data[player.userID] = rp;
        else _balances.Data[player.userID] += rp;
        
        LogToFile("Exchange", $"{player.displayName} exchanged {economics} Economics for {rp} RP", this, true, true);
        
        SendPointsUpdated(player.userID);
        
        CreateExchangeMenu(user, 0, 0);
        
        CreateToast(user, ToastType.Info, user.Translate("UI.Exchange.Success"), user.Translate("UI.Exchange.Success.EconomicsToRP", economics, rp));
    }

    #endregion

    #region Helper
    private static class Colors
    {
        public const string Clear = "0 0 0 0";
        public const string White = "1 1 1 1";
        public const string TransparentWhite = "1 1 1 0.5";
        public const string BarelyVisible = "1 1 1 0.01";
        public const string BarelyVisibleDark = "0 0 0 0.35";
    }

    private static class Sprites
    {
        public const string Close = "assets/icons/close.png";
        public const string Rounded = "assets/content/ui/ui.rounded.tga";
        public const string BackgroundTile = "assets/content/ui/ui.background.tile.psd";
        public const string IconRust = "assets/content/ui/ui.icon.rust.png";
        public const string Cart = "assets/icons/cart.png";
        public const string Occupied = "assets/icons/occupied.png";
        public const string LevelMetal = "assets/icons/level_metal.png";
        public const string Authorize = "assets/icons/authorize.png";
        public const string Info = "assets/icons/info.png";
        public const string Warning = "assets/icons/warning.png";
        public const string MapDollar = "assets/content/ui/map/icon-map_dollar.png";
    }

    private static class Materials
    {
        public const string BackgroundBlur = "assets/content/ui/uibackgroundblur.mat";
        public const string Icon = "assets/icons/iconmaterial.mat";
        public const string GreyOut = "assets/icons/greyout.mat";
    }
    
    private class HorizontalGrid
    {
        private readonly Vector2 _size;
        private readonly Vector2 _spacing;
        private readonly int _perRow;
        private readonly float _viewport;
        
        public HorizontalGrid(Vector2 size, Vector2 spacing, int perRow, float viewport)
        {
            _size = size;
            _spacing = spacing;
            _perRow = perRow;
            _viewport = viewport;
        }
        
        public Offset Get(int index)
        {
            int column = index % _perRow;
            int row = index / _perRow;

            float xMin = (column * (_size.x + _spacing.x));
            float yMax = -(row * (_size.y + _spacing.y));

            return new Offset(xMin, yMax - _size.y, xMin + _size.x, yMax);
        }

        public void ResizeToFit(int count, CuiRectTransformComponent rect)
        {
            int maxRows = Mathf.CeilToInt((float)count / _perRow);
            float height = Mathf.Max(_viewport, (maxRows * _size.y) + (Mathf.Max(maxRows - 1, 0) * _spacing.y));
        
            rect.OffsetMin = $"0 {-(height - _viewport)}";
            rect.OffsetMax = "0 0";
        } 
    }

    private static class UI
    {
        public static CuiElementContainer Container(Layer layer, string name, string color, Anchor anchor, Offset offset, string material = Materials.BackgroundBlur, bool cursorAndKeyboard = true)
        {
            CuiElementContainer container = Pool.Get<CuiElementContainer>();
            
            CuiImageComponent image = Pool.Get<CuiImageComponent>();
            image.Color = color;
            image.Material = material;
            
            CuiRectTransformComponent rect = Pool.Get<CuiRectTransformComponent>();
            rect.AnchorMin = anchor.Min;
            rect.AnchorMax = anchor.Max;
            rect.OffsetMin = offset.Min;
            rect.OffsetMax = offset.Max;
            
            CuiElement element = Pool.Get<CuiElement>();
            element.Name = string.IsNullOrEmpty(name) ? CuiHelper.GetGuid() : name;
            element.Parent = layer.ToString();
            element.DestroyUi = name;
            element.Components.Add(image);
            element.Components.Add(rect);

            if (cursorAndKeyboard)
            {
                CuiNeedsCursorComponent cursor = Pool.Get<CuiNeedsCursorComponent>();
                CuiNeedsKeyboardComponent keyboard = Pool.Get<CuiNeedsKeyboardComponent>();
                element.Components.Add(cursor);
                element.Components.Add(keyboard);
            }

            container.Add(element);
            return container;
        }

        public static void Panel(CuiElementContainer container, string parent, string color, Anchor anchor, Offset offset, string name = "", string material = Materials.GreyOut)
        {
            CuiImageComponent image = Pool.Get<CuiImageComponent>();
            image.Color = color;
            image.Material = material;
            
            CuiRectTransformComponent rect = Pool.Get<CuiRectTransformComponent>();
            rect.AnchorMin = anchor.Min;
            rect.AnchorMax = anchor.Max;
            rect.OffsetMin = offset.Min;
            rect.OffsetMax = offset.Max;
            
            CuiElement element = Pool.Get<CuiElement>();
            element.Name = string.IsNullOrEmpty(name) ? CuiHelper.GetGuid() : name;
            element.Parent = parent;
            element.Components.Add(image);
            element.Components.Add(rect);
            
            container.Add(element);
        }
        
        public static CuiRectTransformComponent ScrollView(CuiElementContainer container, string parent, Anchor anchor, Offset offset, 
            Anchor contentAnchor, Offset contentOffset, CuiScrollbar horizontal = null, CuiScrollbar vertical = null, string name = "")
        {
            CuiImageComponent image = Pool.Get<CuiImageComponent>();
            image.Color = Colors.Clear;
            
            CuiRectTransformComponent rect = Pool.Get<CuiRectTransformComponent>();
            rect.AnchorMin = anchor.Min;
            rect.AnchorMax = anchor.Max;
            rect.OffsetMin = offset.Min;
            rect.OffsetMax = offset.Max;
            
            CuiRectTransformComponent contentRect = Pool.Get<CuiRectTransformComponent>();
            contentRect.AnchorMin = contentAnchor.Min;
            contentRect.AnchorMax = contentAnchor.Max;
            contentRect.OffsetMin = contentOffset.Min;
            contentRect.OffsetMax = contentOffset.Max;
            
            CuiScrollViewComponent scrollView = Pool.Get<CuiScrollViewComponent>();
            scrollView.Horizontal = horizontal != null;
            scrollView.Vertical = vertical != null;
            scrollView.MovementType = ScrollRect.MovementType.Elastic;
            scrollView.Elasticity = 0.1f;
            scrollView.Inertia = false;
            scrollView.DecelerationRate = 0.135f;
            scrollView.ScrollSensitivity = 100f;
            scrollView.ContentTransform = contentRect;
            scrollView.HorizontalScrollbar = horizontal;
            scrollView.VerticalScrollbar = vertical;
            
            CuiElement element = Pool.Get<CuiElement>();
            element.Name = string.IsNullOrEmpty(name) ? CuiHelper.GetGuid() : name;
            element.Parent = parent;
            element.Components.Add(image);
            element.Components.Add(rect);
            element.Components.Add(scrollView);
            
            container.Add(element);

            return contentRect;
        }
        
        public static CuiScrollbar Scrollbar(string handleColor, string highlightColor, string pressedColor, string trackColor, 
            string handleSprite = Sprites.BackgroundTile, string trackSprite = Sprites.BackgroundTile, bool invert = false,
            bool autoHide = true, float size = 10f)
        {
            CuiScrollbar scrollbar = Pool.Get<CuiScrollbar>();
            scrollbar.Invert = invert;
            scrollbar.AutoHide = autoHide;
            scrollbar.HandleSprite = handleSprite;
            scrollbar.Size = size;
            scrollbar.HandleColor = handleColor;
            scrollbar.HighlightColor = highlightColor;
            scrollbar.PressedColor = pressedColor;
            scrollbar.TrackColor = trackColor;
            scrollbar.TrackSprite = trackSprite;
            
            return scrollbar;
        }

        public static void Text(CuiElementContainer container, string parent, string text, Anchor anchor, Offset offset, int size = 14, string color = Colors.White, TextAnchor align = TextAnchor.MiddleCenter, VerticalWrapMode wrap = VerticalWrapMode.Truncate)
        {
            CuiTextComponent textComponent = Pool.Get<CuiTextComponent>();
            textComponent.FontSize = size;
            textComponent.Align = align;
            textComponent.Text = text;
            textComponent.Color = color;
            textComponent.VerticalOverflow = wrap;
            
            CuiRectTransformComponent rect = Pool.Get<CuiRectTransformComponent>();
            rect.AnchorMin = anchor.Min;
            rect.AnchorMax = anchor.Max;
            rect.OffsetMin = offset.Min;
            rect.OffsetMax = offset.Max;
            
            CuiElement element = Pool.Get<CuiElement>();
            element.Name = CuiHelper.GetGuid();
            element.Parent = parent;
            element.Components.Add(textComponent);
            element.Components.Add(rect);
            
            container.Add(element);
        }

        public static void Countdown(CuiElementContainer container, string parent, string text, int startTime, int endTime, Anchor anchor, Offset offset, int size = 14, string color = Colors.White, TextAnchor align = TextAnchor.MiddleCenter, TimerFormat format = TimerFormat.HoursMinutesSeconds)
        {
            CuiTextComponent textComponent = Pool.Get<CuiTextComponent>();
            textComponent.FontSize = size;
            textComponent.Align = align;
            textComponent.Text = text;
            textComponent.Color = color;
            
            CuiCountdownComponent countdown = Pool.Get<CuiCountdownComponent>();
            countdown.StartTime = startTime;
            countdown.EndTime = endTime;
            countdown.TimerFormat = format;
            
            CuiRectTransformComponent rect = Pool.Get<CuiRectTransformComponent>();
            rect.AnchorMin = anchor.Min;
            rect.AnchorMax = anchor.Max;
            rect.OffsetMin = offset.Min;
            rect.OffsetMax = offset.Max;
            
            CuiElement element = Pool.Get<CuiElement>();
            element.Name = CuiHelper.GetGuid();
            element.Parent = parent;
            element.Components.Add(textComponent);
            element.Components.Add(rect);
            element.Components.Add(countdown);
            
            container.Add(element);
        }

        public static void Button(CuiElementContainer container, string parent, string command, Anchor anchor, Offset offset)
        {
            CuiButtonComponent button = Pool.Get<CuiButtonComponent>();
            button.Command = command;
            button.Color = Colors.Clear;
            
            CuiRectTransformComponent rect = Pool.Get<CuiRectTransformComponent>();
            rect.AnchorMin = anchor.Min;
            rect.AnchorMax = anchor.Max;
            rect.OffsetMin = offset.Min;
            rect.OffsetMax = offset.Max;
            
            CuiElement element = Pool.Get<CuiElement>();
            element.Name = CuiHelper.GetGuid();
            element.Parent = parent;
            element.Components.Add(button);
            element.Components.Add(rect);
            
            container.Add(element);
        }

        public static void PNG(CuiElementContainer container, string parent, string png, Anchor anchor, Offset offset)
        {
            CuiImageComponent image = Pool.Get<CuiImageComponent>();
            image.Png = png;
            image.Material = Materials.GreyOut;
            
            CuiRectTransformComponent rect = Pool.Get<CuiRectTransformComponent>();
            rect.AnchorMin = anchor.Min;
            rect.AnchorMax = anchor.Max;
            rect.OffsetMin = offset.Min;
            rect.OffsetMax = offset.Max;
            
            CuiElement element = Pool.Get<CuiElement>();
            element.Name = CuiHelper.GetGuid();
            element.Parent = parent;
            element.Components.Add(image);
            element.Components.Add(rect);
            
            container.Add(element);
        }

        public static void Sprite(CuiElementContainer container, string parent, string sprite, string color, Anchor anchor, Offset offset)
        {
            CuiImageComponent image = Pool.Get<CuiImageComponent>();
            image.Sprite = sprite;
            image.Color = color;
            
            CuiRectTransformComponent rect = Pool.Get<CuiRectTransformComponent>();
            rect.AnchorMin = anchor.Min;
            rect.AnchorMax = anchor.Max;
            rect.OffsetMin = offset.Min;
            rect.OffsetMax = offset.Max;
            
            CuiElement element = Pool.Get<CuiElement>();
            element.Name = CuiHelper.GetGuid();
            element.Parent = parent;
            element.Components.Add(image);
            element.Components.Add(rect);
            
            container.Add(element);
        }
        
        public static void Input(CuiElementContainer container, string parent, string text, string command, Anchor anchor, Offset offset, int size = 14, TextAnchor align = TextAnchor.MiddleLeft)
        {
            CuiInputFieldComponent input = Pool.Get<CuiInputFieldComponent>();
            input.Text = text;
            input.Command = command;
            input.FontSize = size;
            input.Align = align;

            CuiRectTransformComponent rect = Pool.Get<CuiRectTransformComponent>();
            rect.AnchorMin = anchor.Min;
            rect.AnchorMax = anchor.Max;
            rect.OffsetMin = offset.Min;
            rect.OffsetMax = offset.Max;

            CuiElement element = Pool.Get<CuiElement>();
            element.Name = CuiHelper.GetGuid();
            element.Parent = parent;
            element.Components.Add(input);
            element.Components.Add(rect);

            container.Add(element);
        }
        
        public static void Icon(CuiElementContainer container, string parent, int itemId, ulong skinId, Anchor anchor, Offset offset, string name = "")
        {
            CuiImageComponent image = Pool.Get<CuiImageComponent>();
            image.ItemId = itemId;
            image.SkinId = skinId;
            
            CuiRectTransformComponent rect = Pool.Get<CuiRectTransformComponent>();
            rect.AnchorMin = anchor.Min;
            rect.AnchorMax = anchor.Max;
            rect.OffsetMin = offset.Min;
            rect.OffsetMax = offset.Max;
            
            CuiElement element = Pool.Get<CuiElement>();
            element.Name = string.IsNullOrEmpty(name) ? CuiHelper.GetGuid() : name;
            element.Parent = parent;
            element.Components.Add(image);
            element.Components.Add(rect);
            container.Add(element);
        }
    }

    private static class UIPool
    {
        public static void Free(ref CuiElementContainer container)
        {
            for (int index = container.Count - 1; index >= 0; index--)
            {
                CuiElement element = container[index];

                for (int i = element.Components.Count - 1; i >= 0; i--)
                {
                    ICuiComponent component = element.Components[i];

                    switch (component)
                    {
                        case CuiButtonComponent button:
                            Free(ref button);
                            break;
                        case CuiCountdownComponent countdown:
                            Free(ref countdown);
                            break;
                        case CuiImageComponent image:
                            Free(ref image);
                            break;
                        case CuiInputFieldComponent input:
                            Free(ref input);
                            break;
                        case CuiNeedsCursorComponent cursor:
                            Free(ref cursor);
                            break;
                        case CuiNeedsKeyboardComponent keyboard:
                            Free(ref keyboard);
                            break;
                        case CuiOutlineComponent outline:
                            Free(ref outline);
                            break;
                        case CuiRawImageComponent rawImage:
                            Free(ref rawImage);
                            break;
                        case CuiRectTransformComponent rect:
                            Free(ref rect);
                            break;
                        case CuiScrollViewComponent scroll:
                            Free(ref scroll);
                            break;
                        case CuiTextComponent text:
                            Free(ref text);
                            break;
                    }
                }

                Free(ref element);
            }
            
            container.Clear();
            Pool.FreeUnsafe(ref container);
        }
        
        private static void Free(ref CuiElement element)
        {
            element.Name = null;
            element.Parent = null;
            element.DestroyUi = null;
            element.Components.Clear();
            element.FadeOut = 0f;
            element.Update = false;
            Pool.FreeUnsafe(ref element);
        }

        private static void Free(ref CuiButtonComponent button)
        {
            button.Command = null;
            button.Color = Colors.White;
            button.Close = null;
            button.Sprite = Sprites.BackgroundTile;
            button.Material = Materials.Icon;
            button.ImageType = Image.Type.Simple;
            button.FadeIn = 0f;
            Pool.FreeUnsafe(ref button);
        }

        private static void Free(ref CuiCountdownComponent countdown)
        {
            const string NUMBER_FORMAT = "0.####";
            
            countdown.EndTime = 0f;
            countdown.StartTime = 0f;
            countdown.Step = 1f;
            countdown.Interval = 1f;
            countdown.TimerFormat = TimerFormat.None;
            countdown.NumberFormat = NUMBER_FORMAT;
            countdown.DestroyIfDone = true;
            countdown.Command = null;
            countdown.FadeIn = 0f;
            Pool.FreeUnsafe(ref countdown);
        }

        private static void Free(ref CuiImageComponent image)
        {
            image.Sprite = Sprites.BackgroundTile;
            image.Material = Materials.Icon;
            image.Color = Colors.White;
            image.ImageType = Image.Type.Simple;
            image.Png = null;
            image.FadeIn = 0f;
            image.ItemId = 0;
            image.SkinId = 0;
            Pool.FreeUnsafe(ref image);
        }

        private static void Free(ref CuiInputFieldComponent input)
        {
            const string FONT = "RobotoCondensed-Bold.ttf";

            input.Color = Colors.White;
            input.Command = null;
            input.Text = null;
            input.FontSize = 14;
            input.Font = FONT;
            input.Align = TextAnchor.UpperLeft;
            input.CharsLimit = 0;
            input.IsPassword = false;
            input.ReadOnly = false;
            input.NeedsKeyboard = false;
            input.LineType = InputField.LineType.SingleLine;
            input.Autofocus = false;
            input.HudMenuInput = false;
            Pool.FreeUnsafe(ref input);
        }

        private static void Free(ref CuiNeedsCursorComponent cursor)
        {
            Pool.FreeUnsafe(ref cursor);
        }

        private static void Free(ref CuiNeedsKeyboardComponent keyboard)
        {
            Pool.FreeUnsafe(ref keyboard);
        }

        private static void Free(ref CuiOutlineComponent outline)
        {
            const string DISTANCE = "1 -1";
            
            outline.Color = Colors.White;
            outline.Distance = DISTANCE;
            outline.UseGraphicAlpha = false;
            Pool.FreeUnsafe(ref outline);
        }

        private static void Free(ref CuiRawImageComponent rawImage)
        {
            const string ZERO = "0";
            
            rawImage.Color = Colors.White;
            rawImage.Sprite = Sprites.IconRust;
            rawImage.Material = null;
            rawImage.Url = null;
            rawImage.Png = null;
            rawImage.SteamId = ZERO;
            rawImage.FadeIn = 0f;
            Pool.FreeUnsafe(ref rawImage);
        }

        private static void Free(ref CuiRectTransformComponent rect)
        {
            const string ZERO = "0 0";
            const string ONE = "1 1";
            
            rect.AnchorMin = ZERO;
            rect.AnchorMax = ONE;
            rect.OffsetMin = ZERO;
            rect.OffsetMax = ONE;
            Pool.FreeUnsafe(ref rect);
        }

        private static void Free(ref CuiScrollViewComponent scroll)
        {
            CuiRectTransform rect = scroll.ContentTransform;
            if (rect != null)
            {
                Free(ref rect);
                scroll.ContentTransform = null;
            }

            CuiScrollbar horizontalScrollbar = scroll.HorizontalScrollbar;
            if (horizontalScrollbar != null)
            {
                Free(ref horizontalScrollbar);
                scroll.HorizontalScrollbar = null;
            }

            CuiScrollbar verticalScrollbar = scroll.VerticalScrollbar;
            if (verticalScrollbar != null)
            {
                Free(ref verticalScrollbar);
                scroll.VerticalScrollbar = null;
            }

            scroll.Horizontal = false;
            scroll.Vertical = false;
            scroll.MovementType = ScrollRect.MovementType.Elastic;
            scroll.Elasticity = 0.1f;
            scroll.Inertia = false;
            scroll.DecelerationRate = 0.135f;
            scroll.ScrollSensitivity = 1f;
            Pool.FreeUnsafe(ref scroll);
        }

        private static void Free(ref CuiTextComponent text)
        {
            const string FONT = "RobotoCondensed-Bold.ttf";
            
            text.Text = string.Empty;
            text.FontSize = 14;
            text.Font = FONT;
            text.Align = TextAnchor.UpperLeft;
            text.Color = Colors.White;
            text.VerticalOverflow = VerticalWrapMode.Truncate;
            text.FadeIn = 0f;
            Pool.FreeUnsafe(ref text);
        }

        private static void Free(ref CuiRectTransform rect)
        {
            const string ZERO = "0 0";
            const string ONE = "1 1";
            
            rect.AnchorMin = ZERO;
            rect.AnchorMax = ONE;
            rect.OffsetMin = ZERO;
            rect.OffsetMax = ONE;
            Pool.FreeUnsafe(ref rect);
        }

        private static void Free(ref CuiScrollbar scrollbar)
        {
            const string HANDLE_COLOR = "0.15 0.15 0.15 1";
            const string HIGHLIGHT_COLOR = "0.17 0.17 0.17 1";
            const string PRESSED_COLOR = "0.2 0.2 0.2 1";
            const string DEFAULT_TRACK_COLOR = "0.09 0.09 0.09 1";
        
            scrollbar.Invert = false;
            scrollbar.AutoHide = false;
            scrollbar.Size = 20f;
            scrollbar.HandleSprite = Sprites.Rounded;
            scrollbar.TrackSprite = Sprites.BackgroundTile;
            scrollbar.HandleColor = HANDLE_COLOR;
            scrollbar.HighlightColor = HIGHLIGHT_COLOR;
            scrollbar.PressedColor = PRESSED_COLOR;
            scrollbar.TrackColor = DEFAULT_TRACK_COLOR;
            Pool.FreeUnsafe(ref scrollbar);
        }
    }

    private readonly struct Anchor
    {
        private readonly float _xMin;
        private readonly float _yMin;
        private readonly float _xMax;
        private readonly float _yMax;

        public string Min => $"{_xMin} {_yMin}";
        public string Max => $"{_xMax} {_yMax}";

        public Anchor(float xMin, float yMin, float xMax, float yMax)
        {
            _xMin = xMin;
            _yMin = yMin;
            _xMax = xMax;
            _yMax = yMax;
        }

        public static readonly Anchor FullStretch = new Anchor(0f, 0f, 1f, 1f);
        public static readonly Anchor TopStretch = new Anchor(0f, 1f, 1f, 1f);
        public static readonly Anchor BottomStretch = new Anchor(0f, 0f, 1f, 0f);
        public static readonly Anchor LeftStretch = new Anchor(0f, 0f, 0f, 1f);
        
        public static readonly Anchor TopLeft = new Anchor(0f, 1f, 0f, 1f);
        public static readonly Anchor TopCenter = new Anchor(0.5f, 1f, 0.5f, 1f);
        public static readonly Anchor TopRight = new Anchor(1f, 1f, 1f, 1f);
        public static readonly Anchor CenterLeft = new Anchor(0f, 0.5f, 0f, 0.5f);
        public static readonly Anchor Center = new Anchor(0.5f, 0.5f, 0.5f, 0.5f);
        public static readonly Anchor CenterRight = new Anchor(1f, 0.5f, 1f, 0.5f);
        public static readonly Anchor BottomLeft = new Anchor(0f, 0f, 0f, 0f);
    }

    private readonly struct Offset
    {
        private readonly float _xMin;
        private readonly float _yMin;
        private readonly float _xMax;
        private readonly float _yMax;

        public string Min => $"{_xMin} {_yMin}";
        public string Max => $"{_xMax} {_yMax}";

        public Offset(float xMin, float yMin, float xMax, float yMax)
        {
            _xMin = xMin;
            _yMin = yMin;
            _xMax = xMax;
            _yMax = yMax;
        }

        public static readonly Offset zero = new Offset();
    }

    private enum Layer
    {
        Overall,
        Overlay,
        Hud,
        HudMenu,
        Under,
        Inventory,
        Crafting,
        Contacts,
        Clans,
        TechTree,
        Map
    }
    
    #endregion

    #endregion
    
    #region API

    private bool AddPoints(BasePlayer player, int amount) =>
        player && amount > 0 && AddPoints(player.userID, amount);
    
    private bool AddPoints(Core.Libraries.Covalence.IPlayer player, int amount) => 
        player != null && amount > 0 && AddPoints(player.Id, amount);
    
    private bool AddPoints(string userID, int amount) => 
        ulong.TryParse(userID, out ulong result) && AddPoints(result, amount);

    private bool AddPoints(EncryptedValue<ulong> userID, int amount) => 
        AddPoints(userID.Get(), amount);
    
    private bool AddPoints(ulong userID, int amount)
    {
        if (!_balances.Data.ContainsKey(userID))
            _balances.Data[userID] = amount;
        else _balances.Data[userID] += amount;
        
        SendPointsUpdated(userID);
        
        if (Configuration.Options.Logs)
            LogToFile("API", $"{userID} has been given {amount}RP", this, true, true);

        return true;
    }
    
    private bool TakePoints(BasePlayer player, int amount) =>
        player && amount > 0 && TakePoints(player.userID, amount);
    
    private bool TakePoints(Core.Libraries.Covalence.IPlayer player, int amount) => 
        player != null && amount > 0 && TakePoints(player.Id, amount);

    private bool TakePoints(string userID, int amount) => 
        ulong.TryParse(userID, out ulong result) && TakePoints(result, amount);

    private bool TakePoints(EncryptedValue<ulong> userID, int amount) => 
        TakePoints(userID.Get(), amount);
    
    private bool TakePoints(ulong userID, int amount)
    {
        if (!_balances.Data.TryGetValue(userID, out int balance) || amount > balance)
            return false;
        
        _balances.Data[userID] -= amount;

        SendPointsUpdated(userID);
        
        if (Configuration.Options.Logs)
            LogToFile("API", $"{amount}RP has been taken from {userID}", this, true, true);

        return true;
    }
    
    private int CheckPoints(BasePlayer player) => 
        !player ? 0 : CheckPoints(player.userID);

    private int CheckPoints(Core.Libraries.Covalence.IPlayer player) => 
        player == null ? 0 : CheckPoints(player.Id);

    private int CheckPoints(string userID) => 
        ulong.TryParse(userID, out ulong result) ? CheckPoints(result) : 0;

    private int CheckPoints(EncryptedValue<ulong> userID) => 
        CheckPoints(userID.Get());
    
    private int CheckPoints(ulong userID) => 
        !_balances.Data.TryGetValue(userID, out int balance) ? 0 : balance;

    #endregion
    
    #region Hooks

    private void SendPointsUpdated(ulong userID)
    {
        _balances.Data.TryGetValue(userID, out int balance);
        Interface.CallHook("OnPointsUpdated", userID, balance);
    }
    
    #endregion
    
    #region Sellable Items
    
    private void CheckSteamDefinitionsUpdated()
    {
        if ((Steamworks.SteamInventory.Definitions?.Length ?? 0) == 0)
        {
            timer.In(3f, CheckSteamDefinitionsUpdated);
            return;
        }
            
        UpdateSellPriceList();
    }
    
    private void UpdateSellPriceList()
    {
        bool dirty = false;
        
        foreach (ItemDefinition itemDefinition in ItemManager.itemList)
            dirty |= _sellPricing.Data.TryAddItem(itemDefinition.shortname, 0f);
            
        if (dirty)
        {
            _sellPricing.Save();
            Debug.Log("[ServerRewards] Updated sell price list with new items.");
        }
    }

    [ConsoleCommand("sr.sellable")]
    private void CommandSellable(ConsoleSystem.Arg arg)
    {
        if (!IsAdminOrConsole(arg))
            return;

        if (arg.Args?.Length < 2)
            goto SHOW_HELP;

        string shortname = arg.GetString(1, "").ToLower();
        if (string.IsNullOrEmpty(shortname) || !ItemManager.itemDictionaryByName.TryGetValue(shortname, out _))
        {
            SendReply(arg, "[ServerRewards] Empty or invalid item shortname: " + shortname);
            return;
        }

        switch (arg.GetString(0, "").ToLower())
        {
            case "setprice":
                float price = arg.GetFloat(2, 0f);
                ulong skin = arg.GetUInt64(3, 0UL);

                _sellPricing.Data.SetSkinPrice(shortname, skin, price);
                _sellPricing.Save();
                
                SendReply(arg, skin != 0UL ? $"Set skin override price for {shortname} ({skin}) to {price} RP" : $"Set sell price for {shortname} to {price} RP");
                return;
            
            case "setskinmult":
                float skinMult = arg.GetFloat(2, 1.5f);
                
                _sellPricing.Data.SetSkinMultiplier(shortname, skinMult);
                _sellPricing.Save();
                
                SendReply(arg, $"Set skin multiplier for {shortname} to {skinMult}");
                return;
            
            case "show":
                if (!_sellPricing.Data.TryGetSellInfo(shortname, out SellPricing.Info info))
                {
                    SendReply(arg, $"No sell info found for item: {shortname}");
                    return;
                }
                
                SendReply(arg, $"\nSell info for {shortname}:\n" +
                               $"Price: {info.Price} RP\n" +
                               $"Skin Multiplier: {info.SkinMultiplier}\n" +
                               $"Skin Overrides;" +
                               info.SkinOverrides.Select(x => $"\n{x.Key} - {x.Value}").ToSentence());
                return;
            default:
                break;
        }
        
        SHOW_HELP:
        SendReply(arg, "sr.sellable setprice <shortname> <price> <opt:skinID> - Sets the sell price for a item. If you supply a skin ID it the override price for that skin only");
        SendReply(arg, "sr.sellable setskinmult <shortname> <multiplier> - Sets the price multiplier for skin variants. This does not apply to skin override prices");
        SendReply(arg, "sr.sellable show <shortname> - Show the price, skin multiplier and skin override prices for a item");
    }
    
    #endregion

    #region Config

    private static ConfigData Configuration;

    private class ConfigData
    {
        [JsonProperty("Categories")]
        public StoreNavigation Navigation { get; set; }
       
        [JsonProperty("Options")]
        public OtherOptions Options { get; set; }

        [JsonProperty("UI Options")]
        public UIOptions UI { get; set; }

        public VersionNumber Version { get; set; }
        
        public class OtherOptions
        {
            [JsonProperty("Open Store Command")]
            public string StoreCommand { get; set; }
            
            [JsonProperty("Admin RP Command")]
            public string RPCommand { get; set; }

            [JsonProperty("Only allow players to purchase DLC/Skins that they are allowed to use (requires PlayerDLCAPI)")]
            public bool OwnedSkins { get; set; }
            
            [JsonProperty("Hide DLC items in the store")]
            public bool HideDlc { get; set; }

            [JsonProperty("Log all transactions")]
            public bool Logs { get; set; }

            [JsonProperty("Use NPC dealers only")]
            public bool NpcOnly { get; set; }
            
            [JsonProperty("Economics exchange rate (1 RP is worth this much Economics)")]
            public float ExchangeRate { get; set; }
        }

        public class UIOptions
        {
            [JsonProperty("Display user playtime")]
            public bool ShowPlaytime { get; set; }

            [JsonProperty("Toast color (Normal)")]
            public UIColor Toast { get; set; }

            [JsonProperty("Toast text color (Normal)")]
            public UIColor ToastText { get; set; }
            
            [JsonProperty("Toast color (Error)")]
            public UIColor ToastError { get; set; }

            [JsonProperty("Toast text color (Error)")]
            public UIColor ToastTextError { get; set; }

            [JsonProperty("Background color")]
            public UIColor Background { get; set; }

            [JsonProperty("Panel color (Primary)")]
            public UIColor PanelPrimary { get; set; }
            
            [JsonProperty("Panel color (Secondary)")]
            public UIColor PanelSecondary { get; set; }

            [JsonProperty("Scrollbar color (Background)")]
            public UIColor Scrollbar { get; set; }
            
            [JsonProperty("Scrollbar color (Handle)")]
            public UIColor ScrollbarHandle { get; set; }
            
            [JsonProperty("Scrollbar color (Highlight)")]
            public UIColor ScrollbarHighlight { get; set; }
            
            [JsonProperty("Scrollbar color (Pressed)")]
            public UIColor ScrollbarPressed { get; set; }

            [JsonProperty("Button color")]
            public UIColor Button { get; set; }

            [JsonProperty("Button text color")]
            public UIColor ButtonText { get; set; }

            [JsonProperty("Button color (Confirm)")]
            public UIColor ButtonConfirm { get; set; }

            [JsonProperty("Button text color (Confirm)")]
            public UIColor ButtonConfirmText { get; set; }

            [JsonProperty("Button color (Purchase)")]
            public UIColor ButtonPurchase { get; set; }

            [JsonProperty("Button text color (Purchase)")]
            public UIColor ButtonPurchaseText { get; set; }

            [JsonProperty("Button color (Reject)")]
            public UIColor ButtonReject { get; set; }

            [JsonProperty("Button text color (Reject)")]
            public UIColor ButtonRejectText { get; set; }

            [JsonProperty("Button color (Disabled)")]
            public UIColor ButtonDisabled { get; set; }
            
            [JsonProperty("Button text color (Disabled)")]
            public UIColor ButtonDisabledText { get; set; }

            public class UIColor
            {
                public string Hex { get; set; }

                public float Alpha { get; set; }

                public UIColor()
                {
                }

                public UIColor(string hex, float alpha)
                {
                    Hex = hex;
                    Alpha = alpha;
                }

                [JsonIgnore]
                private string _color;

                public static implicit operator string(UIColor color)
                {
                    if (!string.IsNullOrEmpty(color._color))
                        return color._color;

                    string hex = color.Hex.TrimStart('#');

                    int red = int.Parse(hex.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                    int green = int.Parse(hex.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                    int blue = int.Parse(hex.Substring(4, 2), NumberStyles.AllowHexSpecifier);

                    color._color = $"{(double)red / 255} {(double)green / 255} {(double)blue / 255} {color.Alpha}";
                    return color._color;
                }
            }
        }
    }
    
    private class StoreNavigation
    {
        [JsonProperty("Show items tab")]
        public bool Items { get; set; }

        [JsonProperty("Show kits tab")]
        public bool Kits { get; set; }

        [JsonProperty("Show commands tab")]
        public bool Commands { get; set; }

        [JsonProperty("Show exchange tab")]
        public bool Exchange { get; set; }

        [JsonProperty("Show transfer tab")]
        public bool Transfer { get; set; }

        [JsonProperty("Show seller tab")]
        public bool Seller { get; set; }
        
        public StoreNavigation(){}

        public StoreNavigation(StoreNavigation other)
        {
            Items = other.Items;
            Kits = other.Kits;
            Commands = other.Commands;
            Exchange = other.Exchange;
            Transfer = other.Transfer;
            Seller = other.Seller;
        }
    }

    protected override void LoadConfig()
    {
        base.LoadConfig();
        Configuration = Config.ReadObject<ConfigData>();

        if (Configuration.Version < Version)
            UpdateConfigValues();

        Config.WriteObject(Configuration, true);
    }

    protected override void LoadDefaultConfig() => Configuration = GetBaseConfig();

    private ConfigData GetBaseConfig()
    {
        return new ConfigData
        {
            Navigation = new StoreNavigation
            {
                Kits = true,
                Commands = true,
                Items = true,
                Exchange = true,
                Transfer = true,
                Seller = true
            },
            Options = new ConfigData.OtherOptions
            {
                StoreCommand = "s",
                RPCommand = "rp",
                OwnedSkins = true,
                HideDlc = false,
                Logs = true,
                NpcOnly = false,
                ExchangeRate = 1,
            },
            UI = new ConfigData.UIOptions
            {
                ShowPlaytime = true,
                Background = new ConfigData.UIOptions.UIColor("151515", 0.75f),
                PanelPrimary = new ConfigData.UIOptions.UIColor("27241D", 0.9f),
                PanelSecondary = new ConfigData.UIOptions.UIColor("504D48", 0.2f),
                Button = new ConfigData.UIOptions.UIColor("504D48", 0.2f),
                ButtonText = new ConfigData.UIOptions.UIColor("FFFFFF", 1f),
                ButtonDisabled = new ConfigData.UIOptions.UIColor("504D48", 0.2f),
                ButtonDisabledText = new ConfigData.UIOptions.UIColor("FFFFFF", 0.5f),
                ButtonConfirm = new ConfigData.UIOptions.UIColor("526334", 1f),
                ButtonConfirmText = new ConfigData.UIOptions.UIColor("B6F34A", 1f),
                ButtonPurchase = new ConfigData.UIOptions.UIColor("1F6BA0", 1f),
                ButtonPurchaseText = new ConfigData.UIOptions.UIColor("D5EEFF", 1f),
                ButtonReject = new ConfigData.UIOptions.UIColor("CD412B", 0.95f),
                ButtonRejectText = new ConfigData.UIOptions.UIColor("F7EBE1", 0.5f),
                Scrollbar = new ConfigData.UIOptions.UIColor("171717", 1f),
                ScrollbarHandle = new ConfigData.UIOptions.UIColor("6F6B64", 0.25f),
                ScrollbarHighlight = new ConfigData.UIOptions.UIColor("6F6B64", 0.4f),
                ScrollbarPressed = new ConfigData.UIOptions.UIColor("383838", 1f),
                Toast = new ConfigData.UIOptions.UIColor("1F6BA0", 1f),
                ToastText = new ConfigData.UIOptions.UIColor("D5EEFF", 1f),
                ToastError = new ConfigData.UIOptions.UIColor("CD412B", 0.96f),
                ToastTextError = new ConfigData.UIOptions.UIColor("F7EBE1", 0.5f)
            },
            Version = Version
        };
    }

    protected override void SaveConfig() => Config.WriteObject(Configuration, true);

    private void UpdateConfigValues()
    {
        PrintWarning("Config update detected! Updating config values...");

        if (Configuration.Version == default)
            Configuration = GetBaseConfig();

        if (Configuration.Version < new VersionNumber(2, 0, 7))
        {
            Configuration.Options.OwnedSkins = true;
            Configuration.Options.HideDlc = false;
        }
        
        Configuration.Version = Version;
        PrintWarning("Config update completed!");
    }

    #endregion

    #region Data Management

    private Datafile<Hash<ulong, int>> _balances;
    private Datafile<Dictionary<ulong, NpcStore>> _npcStores;
    private Datafile<Products> _products;
    private Datafile<SellPricing> _sellPricing;
    private Datafile<PurchaseCooldowns> _cooldowns;

    public enum ProductType
    {
        None,
        Kit,
        Item,
        Command
    }
    
    private void LoadData()
    {
        _balances = new Datafile<Hash<ulong, int>>("ServerRewards/player_balances");
        _npcStores = new Datafile<Dictionary<ulong, NpcStore>>("ServerRewards/npc_stores");
        _products = new Datafile<Products>("ServerRewards/products");
        _sellPricing = new Datafile<SellPricing>("ServerRewards/sell_prices");
        _cooldowns = new Datafile<PurchaseCooldowns>("ServerRewards/purchase_cooldowns");

        MoveOldDataFilesIfExist();
        ConvertOldData();
    }

    private void MoveOldDataFilesIfExist()
    {
        MoveDataFile("player_data");
        MoveDataFile("cooldown_data");
        MoveDataFile("npc_data");
        MoveDataFile("reward_data");
        MoveDataFile("sale_data");
    }

    private void MoveDataFile(string name)
    {
        string moveFrom = Path.Combine(Interface.Oxide.DataDirectory, "ServerRewards", name + ".json");
        string moveTo = Path.Combine(Interface.Oxide.DataDirectory, "ServerRewards", "v1", name + ".json");
        string folder = Path.Combine(Interface.Oxide.DataDirectory, "ServerRewards", "v1");
        
        if (File.Exists(moveFrom) && !File.Exists(moveTo))
        {
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            
            Debug.Log($"[ServerRewards] Moving old data file /data/ServerRewards/{name}.json to /data/ServerRewards/v1/{name}.json");
            File.Move(moveFrom, moveTo);
        }
    }
    
    private class PurchaseCooldowns
    {
        public Hash<ulong, Hash<int, double>> Users = new Hash<ulong, Hash<int, double>>();

        public void AddCooldown(ulong playerId, int productId, int time)
        {
            if (!Users.TryGetValue(playerId, out Hash<int, double> cooldown))
                Users[playerId] = cooldown = new Hash<int, double>();

            cooldown[productId] = time + CurrentTime();
        }

        public bool HasCooldown(ulong playerId, int productId, out double remaining)
        {
            remaining = 0;
            
            if (!Users.TryGetValue(playerId, out Hash<int, double> cooldown))
                return false;

            if (!cooldown.TryGetValue(productId, out double time))
                return false;

            remaining = time - CurrentTime();
            return remaining > 0;
        }
    }

    private class NpcStore
    {
        public string Name;
        public bool CustomStore;
        public StoreNavigation Navigation;
        public Products Products;
    }

    private class Products
    {
        public int ProductIndex = 0;
        public List<Item> Items = new List<Item>();
        public List<Kit> Kits = new List<Kit>();
        public List<Command> Commands = new List<Command>();

        [JsonIgnore]
        private ItemCategory[] _itemCategories;

        [JsonIgnore]
        public ItemCategory[] ItemCategories
        {
            get
            {
                if (_itemCategories != null)
                    return _itemCategories;

                List<ItemCategory> categories = Pool.Get<List<ItemCategory>>();
                categories.Add(ItemCategory.All);
                foreach (Item item in Items)
                {
                    if (!categories.Contains(item.Category))
                        categories.Add(item.Category);
                }

                _itemCategories = categories.ToArray();
                Pool.FreeUnmanaged(ref categories);

                return _itemCategories;
            }
        }

        public bool TryFindProductByID(ProductType productType, int productId, out Product product)
        {
            T FindProduct<T>(List<T> list, int id) where T : Product
            {
                for (var i = 0; i < list.Count; i++)
                {
                    T t = list[i];
                    if (t.ID == id)
                        return t;
                }

                return null;
            }

            product = productType switch
            {
                ProductType.Item => FindProduct(Items, productId),
                ProductType.Kit => FindProduct(Kits, productId),
                ProductType.Command => FindProduct(Commands, productId),
                _ => null
            };
            
            return product != null;
        }
        
        public bool DeleteByProductID(ProductType productType, int productId)
        {
            bool DeleteProduct<T>(List<T> list, int id) where T : Product
            {
                for (var i = 0; i < list.Count; i++)
                {
                    T t = list[i];
                    if (t.ID == id)
                    {
                        list.RemoveAt(i);
                        return true;
                    }
                }

                return false;
            }
            
            bool result = productType switch
            {
                ProductType.Item => DeleteProduct(Items, productId),
                ProductType.Kit => DeleteProduct(Kits, productId),
                ProductType.Command => DeleteProduct(Commands, productId),
                _ => false
            };

            return result;
        }
        
        public bool AddOrUpdateProduct(Product product)
        {
            ProductType productType = product switch
            {
                Item => ProductType.Item,
                Kit => ProductType.Kit,
                Command => ProductType.Command,
                _ => ProductType.None
            };
            
            if (product.ID >= 0)
            {
                if (!TryFindProductByID(productType, product.ID, out Product existing))
                    return false;
                
                existing.CopyFrom(product);
                return true;
            }
            
            product.ID = ProductIndex++;
            
            if (productType == ProductType.Item)
                _itemCategories = null;
            
            switch (productType)
            {
                case ProductType.Kit:
                    Kits.Add(product as Kit);
                    return true;
                case ProductType.Item:
                    Items.Add(product as Item);
                    return true;
                case ProductType.Command:
                    Commands.Add(product as Command);
                    return true;
                default:
                    return false;
            }
        }
        
        public void RegisterPermissions(Permission permission, Plugin plugin)
        {
            void RegisterPermissionIfApplicable(Product product, Permission permission, Plugin plugin)
            {
                if (!string.IsNullOrEmpty(product.Permission))
                    permission.RegisterPermission(product.Permission, plugin);
            }

            foreach (Item item in Items)
                RegisterPermissionIfApplicable(item, permission, plugin);
            
            foreach (Kit kit in Kits)
                RegisterPermissionIfApplicable(kit, permission, plugin);
            
            foreach (Command command in Commands)
                RegisterPermissionIfApplicable(command, permission, plugin);
        }

        public class Item : Product
        {
            public string Shortname;
            public int Amount;
            public ulong SkinId;
            public bool IsBp;
            public bool IgnoreDlcCheck;
            public ItemCategory Category;

            [JsonIgnore]
            public override string PurchaseName => Amount > 0 ? $"{Amount} x {DisplayName}" : DisplayName;

            [JsonIgnore]
            private ItemDefinition _itemDefinition;

            [JsonIgnore]
            public ItemDefinition ItemDefinition
            {
                get
                {
                    if (!_itemDefinition)
                        _itemDefinition = ItemManager.FindItemDefinition(Shortname);
                    return _itemDefinition;
                }
            }
            
            [JsonIgnore]
            private static ItemDefinition _blueprintBase;

            [JsonIgnore]
            public static ItemDefinition BlueprintBase
            {
                get
                {
                    if (!_blueprintBase)
                        _blueprintBase = ItemManager.FindItemDefinition("blueprintbase");
                    return _blueprintBase;
                }
            }
            
            public Item(){}

            public Item(Product product) => product.CopyTo(this);

            public override void CopyTo(Product product)
            {
                base.CopyTo(product);
                if (product is Item item)
                {
                    item.Shortname = Shortname;
                    item.Amount = Amount;
                    item.SkinId = SkinId;
                    item.IsBp = IsBp;
                    item.Category = Category;
                    item.IgnoreDlcCheck = IgnoreDlcCheck;
                }
            }
            
            public override void CopyFrom(Product product)
            {
                base.CopyFrom(product);
                if (product is Item item)
                {
                    Shortname = item.Shortname;
                    Amount = item.Amount;
                    SkinId = item.SkinId;
                    IsBp = item.IsBp;
                    Category = item.Category;
                    IgnoreDlcCheck = item.IgnoreDlcCheck;
                    _itemDefinition = null; // Reset to ensure it gets reloaded
                }
            }

            public override bool HasRequiredFields()
            {
                if (string.IsNullOrEmpty(Shortname))
                    return false;

                if (string.IsNullOrEmpty(DisplayName))
                    return false;
                
                if (Amount <= 0)
                    return false;

                return true;
            }

            public override void SetField(string fieldName, string value)
            {
                base.SetField(fieldName, value);

                switch (fieldName)
                {
                    case nameof(Shortname):
                    {
                        ItemDefinition itemDefinition = ItemManager.FindItemDefinition(value);
                        if (!itemDefinition)
                        {
                            Shortname = "rifle.ak";
                            Category = ItemCategory.Weapon;
                            DisplayName = "Assault Rifle";
                            SkinId = 0UL;
                            _itemDefinition = null;
                            break;
                        }
        
                        Shortname = value;
                        Category = itemDefinition.category;
                        DisplayName = itemDefinition.displayName.english;
                        SkinId = 0UL;
                        _itemDefinition = itemDefinition;
                        break;
                    }
                    
                    case nameof(Amount):
                        Amount = int.TryParse(value, out int amount) ? Mathf.Max(1, amount) : 0;
                        break;
                    
                    case nameof(SkinId):
                        SkinId = ulong.TryParse(value, out ulong skinId) ? skinId : 0UL;
                        break;
                    
                    case nameof(IsBp):
                        IsBp = bool.TryParse(value, out bool isBp) && isBp;
                        break;
                    
                    case nameof(IgnoreDlcCheck):
                        IgnoreDlcCheck = bool.TryParse(value, out bool ignoreDlcCheck) && ignoreDlcCheck;
                        break;
                }
            }

            public override bool GiveToPlayer(BasePlayer player)
            {
                ItemDefinition itemDefinition = ItemDefinition;
                if (!itemDefinition)
                {
                    Debug.LogError($"[ServerRewards] Item definition not found for shortname: {Shortname}");
                    return false;
                }

                global::Item item = null;
                if (IsBp)
                {
                    item = ItemManager.Create(BlueprintBase, Amount);
                    item.blueprintTarget = itemDefinition.itemid;
                }
                else item = ItemManager.Create(itemDefinition, Amount, SkinId);
                player.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);
                return true;
            }
        }

        public class Kit : Product
        {
            public string KitName;
            public string Description;
            
            [JsonIgnore]
            private KitData _kitData;

            [JsonIgnore]
            public KitData KitData
            {
                get
                {
                    if (_kitData == null)
                    {
                        JObject jObject = _getKitData(KitName);
                        if (jObject == null)
                            return null;

                        _kitData = jObject.ToObject<KitData>();
                    }

                    return _kitData;
                }
            }
            
            public Kit(){}

            public Kit(Product product) => product.CopyTo(this);

            public override void CopyTo(Product product)
            {
                base.CopyTo(product);
                if (product is Kit kit)
                {
                    kit.KitName = KitName;
                    kit.Description = Description;
                }
            }
            
            public override void CopyFrom(Product product)
            {
                base.CopyFrom(product);
                if (product is Kit kit)
                {
                    KitName = kit.KitName;
                    Description = kit.Description;
                    _kitData = null;
                }
            }

            public override bool HasRequiredFields()
            {
                if (string.IsNullOrEmpty(KitName))
                    return false;

                if (string.IsNullOrEmpty(DisplayName))
                    return false;

                return true;
            }

            public override void SetField(string fieldName, string value)
            {
                base.SetField(fieldName, value);
                
                switch (fieldName)
                {
                    case nameof(KitName):
                        KitName = value;
                        if (!string.IsNullOrEmpty(KitName))
                        {
                            if (!_isKit(value))
                            {
                                KitName = string.Empty;
                                DisplayName = string.Empty;
                            }
                            else
                            {
                                DisplayName = value;
                                Description = _getKitDescription(value);
                                IconURL = _getKitImage(value);
                            }
                        }
                        _kitData = null;
                        break;
                    
                    case nameof(Description):
                        Description = value;
                        break;
                    
                    default: break;
                }
            }

            public override bool GiveToPlayer(BasePlayer player)
            {
                if (string.IsNullOrEmpty(KitName) || !_isKit(KitName))
                    return false;

                return _giveKit(player, KitName) is true;
            }
        }

        public class Command : Product
        {
            public string Description;
            public List<string> Commands = new List<string>();

            public Command(){}
            
            public Command(Product product) => product.CopyTo(this);
            
            public override void CopyTo(Product product)
            {
                base.CopyTo(product);
                if (product is Command command)
                {
                    command.Description = Description;
                    command.Commands.Clear();
                    command.Commands.AddRange(Commands);
                }
            }
            
            public override void CopyFrom(Product product)
            {
                base.CopyFrom(product);
                if (product is Command command)
                {
                    Description = command.Description;
                    Commands.Clear();
                    Commands.AddRange(command.Commands);
                }
            }

            public override bool HasRequiredFields()
            {
                if (string.IsNullOrEmpty(DisplayName))
                    return false;

                return Commands.Count > 0;
            }

            public override void SetField(string fieldName, string value)
            {
                base.SetField(fieldName, value);

                switch (fieldName)
                {
                    case nameof(Description):
                        Description = value;
                        break;
                    
                    default: break;
                }
            }

            public void SetCommand(SetCommandType commandType, int index, string value)
            {
                switch (commandType)
                {
                    case SetCommandType.Add:
                        Commands.Add(value);
                        break;
                    case SetCommandType.Edit:
                        Commands[index] = value;
                        break;
                    case SetCommandType.Remove:
                        if (index >= 0 && index < Commands.Count)
                            Commands.RemoveAt(index);
                        break;
                }
            }

            public override bool GiveToPlayer(BasePlayer player)
            {
                foreach (string command in Commands)
                {
                    string cmd = command
                        .Replace("$player.id", player.UserIDString)
                        .Replace("$player.name", player.displayName)
                        .Replace("$player.x", player.transform.position.x.ToString(CultureInfo.CurrentCulture))
                        .Replace("$player.y", player.transform.position.y.ToString(CultureInfo.CurrentCulture))
                        .Replace("$player.z", player.transform.position.z.ToString(CultureInfo.CurrentCulture));
                    
                    ConsoleSystem.Run(ConsoleSystem.Option.Server, cmd);
                }

                return true;
            }
        }

        public abstract class Product
        {
            public int ID = -1;
            public string DisplayName;
            public int Cost;
            public int Cooldown;
            public string IconURL;
            public string Permission;

            [JsonIgnore]
            public virtual string PurchaseName => DisplayName;

            public virtual void CopyTo(Product product)
            {
                product.ID = ID;
                product.DisplayName = DisplayName;
                product.Cost = Cost;
                product.Cooldown = Cooldown;
                product.IconURL = IconURL;
                product.Permission = Permission;
            }

            public virtual void CopyFrom(Product product)
            {
                ID = product.ID;
                DisplayName = product.DisplayName;
                Cost = product.Cost;
                Cooldown = product.Cooldown;
                IconURL = product.IconURL;
                Permission = product.Permission;
            }

            public abstract bool HasRequiredFields();

            public virtual void SetField(string fieldName, string value)
            {
                switch (fieldName)
                {
                    case nameof(DisplayName):
                        DisplayName = value;
                        break;
                    
                    case nameof(Cost):
                        if (int.TryParse(value, out int cost))
                            Cost = Mathf.Max(0, cost);
                        break;
                    
                    case nameof(Cooldown):
                        if (int.TryParse(value, out int cooldown))
                            Cooldown = Mathf.Max(0, cooldown);
                        break;
                    
                    case nameof(IconURL):
                        IconURL = IsUrl(value) ? value : string.Empty;
                        break;
                    
                    case nameof(Permission):
                        const string PERMISSION_PREFIX = "serverrewards.";
                        if (!string.IsNullOrEmpty(value) && !value.StartsWith(PERMISSION_PREFIX))
                            value = PERMISSION_PREFIX + value;
                        Permission = value;
                        break;
                    
                    default: break;
                }
            }

            public abstract bool GiveToPlayer(BasePlayer player);
        }
    }
    
    private class SellPricing
    {
        public SortedDictionary<string, Info> Items = new SortedDictionary<string, Info>();

        public bool TryGetSellPrice(string shortname, ulong skinId, out float price)
        {
            price = 0;
            if (!Items.TryGetValue(shortname, out Info info))
                return false;
            
            price = info.Price;

            if (skinId != 0UL)
            {
                if (info.SkinOverrides.TryGetValue(skinId, out float skinPrice))
                    price = skinPrice;
                else price *= info.SkinMultiplier;
            }

            return true;
        }
        
        public bool TryGetSellInfo(string shortname, out Info info) => Items.TryGetValue(shortname, out info);

        public bool TryAddItem(string shortname, float price)
        {
            if (Items.TryGetValue(shortname, out Info info))
                return false;
            
            Items[shortname] = new Info(price);

            return true;
        }

        public void SetSkinPrice(string shortname, ulong skinId, float price)
        {
            if (!Items.TryGetValue(shortname, out Info info))
                info = Items[shortname] = new Info();
            
            if (skinId != 0UL)
                info.SkinOverrides[skinId] = price;
            else info.Price = price;
        }

        public void SetSkinMultiplier(string shortname, float multiplier)
        {
            if (!Items.TryGetValue(shortname, out Info info))
                info = Items[shortname] = new Info(0f);

            info.SkinMultiplier = multiplier;
        }
        
        public class Info
        {
            public float Price = 0;
            
            [JsonProperty("Price multiplier for skin variants (excluding overrides)")]
            public float SkinMultiplier = 1.5f;
            
            [JsonProperty("Skin Overrides (Skin ID/Override Price)")]
            public Hash<ulong, float> SkinOverrides = new Hash<ulong, float>();
            
            public Info(){}

            public Info(float price, float skinMultiplier = 1.5f)
            {
                Price = price;
                SkinMultiplier = skinMultiplier;
            }
        }
    }
    
    private class Datafile<T>
    {
        private readonly string name;

        protected DynamicConfigFile dynamicConfigFile;

        public T Data;
        
        public bool JustCreated { get; private set; }

        public Datafile(string name, params JsonConverter[] converters)
        {
            this.name = name;

            dynamicConfigFile = Interface.Oxide.DataFileSystem.GetFile(name);

            dynamicConfigFile.Settings.Converters.Clear();
            dynamicConfigFile.Settings.Converters.Add(new KeyValuesConverter());

            if (converters != null)
            {
                foreach (JsonConverter jsonConverter in converters)
                    dynamicConfigFile.Settings.Converters.Add(jsonConverter);
            }

            Load();
        }

        private void Load()
        {
            string filename = CheckPath(dynamicConfigFile.Filename);
            if (dynamicConfigFile.Exists(filename))
            {
                Data = JsonConvert.DeserializeObject<T>(File.ReadAllText(filename), dynamicConfigFile.Settings);
            }
            else
            {
                JustCreated = true;
                Data = Activator.CreateInstance<T>();
                dynamicConfigFile.WriteObject<T>(Data, filename: filename);
            }
        }
        
        private string CheckPath(string filename)
        {
            filename = DynamicConfigFile.SanitizeName(filename);
            string fullPath = Path.GetFullPath(filename);
            return fullPath.StartsWith(Interface.Oxide.InstanceDirectory, StringComparison.Ordinal) ? fullPath : throw new Exception("Only access to oxide directory!\nPath: " + fullPath);
        }
        
        public void Save()
        {
            dynamicConfigFile.WriteObject(Data);
        }
    }

    #region Conversion

    [ConsoleCommand("sr.convert")]
    private void CommandConvertData(ConsoleSystem.Arg arg)
    {
        if (arg.Connection != null)
        {
            BasePlayer player = arg.Player();
            if (!player || !IsAdmin(player))
                return;
        }
        
        ConvertOldData(true);
    }
    
    private void ConvertOldData(bool force = false)
    {
        DataFileSystem dfs = Interface.Oxide.DataFileSystem;
        
        OldPlayerData playerData = dfs.ReadObject<OldPlayerData>("ServerRewards/v1/player_data");
        OldCooldownData cooldownData = dfs.ReadObject<OldCooldownData>("ServerRewards/v1/cooldown_data");
        OldNPCData npcData = dfs.ReadObject<OldNPCData>("ServerRewards/v1/npc_data");
        OldRewardData rewardData = dfs.ReadObject<OldRewardData>("ServerRewards/v1/reward_data");
        OldSaleData saleData = dfs.ReadObject<OldSaleData>("ServerRewards/v1/sale_data");
        
        if ((_products.JustCreated || force) && rewardData != null)
            ConvertRewardData(rewardData);
        
        if ((_balances.JustCreated || force) && playerData != null)
            ConvertPlayerData(playerData);
        
        if ((_sellPricing.JustCreated || force) && saleData != null)
            ConvertSaleData(saleData);
        
        if ((_npcStores.JustCreated || force) && rewardData != null && npcData != null)
            ConvertNpcData(npcData, rewardData);
    }

    private void ConvertRewardData(OldRewardData rewardData)
    {
        Debug.Log("[ServerRewards] Converting old reward data to v2.x.x format...");
        foreach (OldRewardData.OldRewardItem item in rewardData.items.Values)
        {
            _products.Data.Items.Add(new Products.Item
            {
                ID = _products.Data.ProductIndex,
                Amount = item.amount,
                Cost = item.cost,
                Cooldown = item.cooldown,
                IconURL = item.customIcon,
                DisplayName = item.displayName,
                IsBp = item.isBp,
                Shortname = item.shortname,
                SkinId = item.skinId,
                Category = OldRewardData.CategoryToItemCategory(item.category),
            });
            
            _products.Data.ProductIndex++;
        }
        
        foreach (OldRewardData.OldRewardKit kit in rewardData.kits.Values)
        {
            _products.Data.Kits.Add(new Products.Kit
            {
                ID = _products.Data.ProductIndex,
                KitName = kit.kitName,
                Description = kit.description,
                IconURL = kit.iconName,
                DisplayName = kit.kitName,
                Cost = kit.cost,
                Cooldown = kit.cooldown
            });
            
            _products.Data.ProductIndex++;
        }
        
        foreach (OldRewardData.OldRewardCommand command in rewardData.commands.Values)
        {
            _products.Data.Commands.Add(new Products.Command
            {
                ID = _products.Data.ProductIndex,
                DisplayName = command.displayName,
                Description = command.description,
                IconURL = command.iconName,
                Cost = command.cost,
                Cooldown = command.cooldown,
                Commands = new List<string>(command.commands)
            });
            
            _products.Data.ProductIndex++;
        }
        
        _products.Save();
        Debug.Log($"[ServerRewards] Converted {_products.Data.Items.Count} items, {_products.Data.Kits.Count} kits and {_products.Data.Commands.Count} commands!");
    }
    
    private void ConvertPlayerData(OldPlayerData playerData)
    {
        Debug.Log("[ServerRewards] Converting old player balance data to v2.x.x format...");
        foreach (KeyValuePair<ulong, int> entry in playerData.playerRP)
        {
            _balances.Data[entry.Key] = entry.Value;
        }
        
        _balances.Save();
        Debug.Log($"[ServerRewards] Converted {_balances.Data.Count} player balances!");
    }

    private void ConvertSaleData(OldSaleData saleData)
    {
        Debug.Log("[ServerRewards] Converting old sale data to v2.x.x format...");
        foreach (KeyValuePair<string, Dictionary<ulong, OldSaleData.OldSaleItem>> nameSkinPair in saleData.items)
        {
            if (nameSkinPair.Value.TryGetValue(0UL, out OldSaleData.OldSaleItem item))
                _sellPricing.Data.TryAddItem(nameSkinPair.Key, item.price);
            
            foreach (KeyValuePair<ulong, OldSaleData.OldSaleItem> skinInfoPair in nameSkinPair.Value)
            {
                if (skinInfoPair.Key != 0UL)
                    _sellPricing.Data.SetSkinPrice(nameSkinPair.Key, skinInfoPair.Key, skinInfoPair.Value.price);
            }
        }
        
        _sellPricing.Save();
        Debug.Log($"[ServerRewards] Converted {_balances.Data.Count} player balances!");
    }

    private void ConvertNpcData(OldNPCData npcData, OldRewardData rewardData)
    {
        Debug.Log("[ServerRewards] Converting old NPC data to v2.x.x format...");
        foreach (KeyValuePair<string, OldNPCData.OldNPCInfo> kvp in npcData.npcInfo)
        {
            if (!ulong.TryParse(kvp.Key, out ulong userID))
                continue;
            
            NpcStore npcStore = new NpcStore
            {
                Name = kvp.Value.name,
                Navigation = new StoreNavigation
                {
                    Kits = !kvp.Value.useCustom || kvp.Value.sellKits,
                    Commands = !kvp.Value.useCustom || kvp.Value.sellCommands,
                    Items = !kvp.Value.useCustom || kvp.Value.sellItems,
                    Exchange = !kvp.Value.useCustom || kvp.Value.canExchange,
                    Transfer = !kvp.Value.useCustom || kvp.Value.canTransfer,
                    Seller = !kvp.Value.useCustom || kvp.Value.canSell
                },
                CustomStore = kvp.Value.useCustom,
                Products = new Products()
            };

            if (kvp.Value.useCustom)
            {
                foreach (string itemId in kvp.Value.items)
                {
                    if (rewardData.items.TryGetValue(itemId, out OldRewardData.OldRewardItem oldItem))
                    {
                        npcStore.Products.Items.Add(new Products.Item
                        {
                            ID = npcStore.Products.ProductIndex++,
                            Amount = oldItem.amount,
                            Cost = oldItem.cost,
                            Cooldown = oldItem.cooldown,
                            IconURL = oldItem.customIcon,
                            DisplayName = oldItem.displayName,
                            IsBp = oldItem.isBp,
                            Shortname = oldItem.shortname,
                            SkinId = oldItem.skinId,
                            Category = OldRewardData.CategoryToItemCategory(oldItem.category)
                        });
                    }
                }

                foreach (string kitId in kvp.Value.kits)
                {
                    if (rewardData.kits.TryGetValue(kitId, out OldRewardData.OldRewardKit kit))
                    {
                        npcStore.Products.Kits.Add(new Products.Kit
                        {
                            ID = npcStore.Products.ProductIndex++,
                            KitName = kit.kitName,
                            Description = kit.description,
                            IconURL = kit.iconName,
                            DisplayName = kit.kitName,
                            Cost = kit.cost,
                            Cooldown = kit.cooldown
                        });
                    }
                }

                foreach (string commandId in kvp.Value.commands)
                {
                    if (rewardData.commands.TryGetValue(commandId, out OldRewardData.OldRewardCommand command))
                    {
                        npcStore.Products.Commands.Add(new Products.Command
                        {
                            ID = npcStore.Products.ProductIndex++,
                            DisplayName = command.displayName,
                            Description = command.description,
                            IconURL = command.iconName,
                            Cost = command.cost,
                            Cooldown = command.cooldown,
                            Commands = new List<string>(command.commands)
                        });
                    }
                }
            }

            _npcStores.Data.Add(userID, npcStore);
        }
        
        _npcStores.Save();
        Debug.Log($"[ServerRewards] Converted {_npcStores.Data.Count} player balances!");
    }
    
    private class OldPlayerData
    {
        public Dictionary<ulong, int> playerRP = new Dictionary<ulong, int>();
    }

    private class OldCooldownData
    {
        public Dictionary<ulong, OldCooldownUser> users = new Dictionary<ulong, OldCooldownUser>();

        public class OldCooldownUser
        {
            public Dictionary<ProductType, Dictionary<string, double>> items = new Dictionary<ProductType, Dictionary<string, double>>
            {
                [ProductType.Command] = new Dictionary<string, double>(),
                [ProductType.Item] = new Dictionary<string, double>(),
                [ProductType.Kit] = new Dictionary<string, double>()
            };
        }
    }

    private class OldNPCData
    {
        public Dictionary<string, OldNPCInfo> npcInfo = new Dictionary<string, OldNPCInfo>();

        public class OldNPCInfo
        {
            public string name;
            public float x, z;
            public bool useCustom, sellItems, sellKits, sellCommands, canTransfer, canSell, canExchange;
            public List<string> items = new List<string>();
            public List<string> kits = new List<string>();
            public List<string> commands = new List<string>();
        }
    }

    private class OldRewardData
    {
        public Dictionary<string, OldRewardItem> items = new Dictionary<string, OldRewardItem>();
        public SortedDictionary<string, OldRewardKit> kits = new SortedDictionary<string, OldRewardKit>();
        public SortedDictionary<string, OldRewardCommand> commands = new SortedDictionary<string, OldRewardCommand>();

        public enum Category
        {
            None,
            Weapon,
            Construction,
            Items,
            Resources,
            Attire,
            Tool,
            Medical,
            Food,
            Ammunition,
            Traps,
            Misc,
            Component,
            Electrical,
            Fun
        }

        public static ItemCategory CategoryToItemCategory(Category category)
        {
            return category switch
            {
                Category.Weapon => ItemCategory.Weapon,
                Category.Construction => ItemCategory.Construction,
                Category.Items => ItemCategory.Items,
                Category.Resources => ItemCategory.Resources,
                Category.Attire => ItemCategory.Attire,
                Category.Tool => ItemCategory.Tool,
                Category.Medical => ItemCategory.Medical,
                Category.Food => ItemCategory.Food,
                Category.Ammunition => ItemCategory.Ammunition,
                Category.Traps => ItemCategory.Traps,
                Category.Misc => ItemCategory.Misc,
                Category.Component => ItemCategory.Component,
                Category.Electrical => ItemCategory.Electrical,
                Category.Fun => ItemCategory.Fun,
                _ => ItemCategory.All
            };
        }

        public class OldRewardItem : OldReward
        {
            public string shortname, customIcon;
            public int amount;
            public ulong skinId;
            public bool isBp;
            public Category category;
        }

        public class OldRewardKit : OldReward
        {
            public string kitName, description, iconName;
        }

        public class OldRewardCommand : OldReward
        {
            public string description, iconName;
            public List<string> commands = new List<string>();
        }

        public class OldReward
        {
            public string displayName;
            public int cost;
            public int cooldown;
        }
    }

    private class OldSaleData
    {
        public Dictionary<string, Dictionary<ulong, OldSaleItem>> items = new Dictionary<string, Dictionary<ulong, OldSaleItem>>();

        public class OldSaleItem
        {
            public float price = 0;
            public string displayName;
            public bool enabled = false;
        }
    }

    #endregion
    #endregion
    
    #region Localization

    private readonly Dictionary<string, string> _messages = new Dictionary<string, string>
    {
        ["UI.Title"] = "SERVER REWARDS",
        ["UI.Category.Items"] = "ITEMS",
        ["UI.Category.Kits"] = "KITS",
        ["UI.Category.Commands"] = "COMMANDS",
        ["UI.Category.Exchange"] = "EXCHANGE",
        ["UI.Category.Transfer"] = "TRANSFER",
        ["UI.Category.Sell"] = "SELL",
        ["UI.Items"] = "ITEMS",
        ["UI.Kits"] = "KITS",
        ["UI.Commands"] = "COMMANDS",
        ["UI.Exchange"] = "EXCHANGE",
        ["UI.Transfer"] = "TRANSFER",
        ["UI.Sell"] = "SELL",
        ["UI.ItemCategory.Weapon"] = "WEAPON",
        ["UI.ItemCategory.Construction"] = "CONSTRUCTION",
        ["UI.ItemCategory.Items"] = "ITEMS",
        ["UI.ItemCategory.Resources"] = "RESOURCES",
        ["UI.ItemCategory.Attire"] = "ATTIRE",
        ["UI.ItemCategory.Tool"] = "TOOL",
        ["UI.ItemCategory.Medical"] = "MEDICAL",
        ["UI.ItemCategory.Food"] = "FOOD",
        ["UI.ItemCategory.Ammunition"] = "AMMUNITION",
        ["UI.ItemCategory.Traps"] = "TRAPS",
        ["UI.ItemCategory.Misc"] = "MISC",
        ["UI.ItemCategory.All"] = "ALL",
        ["UI.ItemCategory.Common"] = "COMMON",
        ["UI.ItemCategory.Component"] = "COMPONENT",
        ["UI.ItemCategory.Search"] = "SEARCH",
        ["UI.ItemCategory.Favourite"] = "FAVOURITE",
        ["UI.ItemCategory.Electrical"] = "ELECTRICAL",
        ["UI.ItemCategory.Fun"] = "FUN",
        ["UI.AdminMode"] = "ADMIN MODE",
        ["UI.AddProduct"] = "ADD PRODUCT",
        ["UI.EditProduct"] = "EDIT PRODUCT",
        ["UI.CreateProduct"] = "CREATE PRODUCT",
        ["UI.Cancel"] = "CANCEL",
        ["UI.Confirm"] = "CONFIRM",
        ["UI.RP"] = "RP: {0}",
        ["UI.Playtime"] = "PLAYTIME: {0}",
        ["UI.Kit.Belt"] = "BELT",
        ["UI.Kit.Wear"] = "WEAR",
        ["UI.Kit.Main"] = "MAIN",
        ["UI.NotSellable"] = "NOT SELLABLE",
        ["UI.SellPrice"] = "{0}RP",
        ["UI.Fields.Item"] = "ITEM",
        ["UI.Fields.Amount"] = "AMOUNT",
        ["UI.Fields.Skin"] = "SKIN ID",
        ["UI.Fields.Blueprint"] = "BLUEPRINT",
        ["UI.Fields.IgnoreDlcCheck"] = "IGNORE DLC",
        ["UI.Fields.DisplayName"] = "DISPLAY NAME",
        ["UI.Fields.Description"] = "DESCRIPTION",
        ["UI.Fields.Cost"] = "COST",
        ["UI.Fields.Cooldown"] = "COOLDOWN (SEC)",
        ["UI.Fields.IconUrl"] = "ICON URL",
        ["UI.Fields.Kit"] = "KIT",
        ["UI.Fields.Command"] = "COMMAND {0}",
        ["UI.Fields.Permission"] = "PERMISSION",
        ["UI.SelectItem"] = "SELECT ITEM",
        ["UI.SelectKit"] = "SELECT KIT",
        ["UI.SelectPlayer"] = "SELECT PLAYER",
        ["UI.True"] = "TRUE",
        ["UI.False"] = "FALSE",
        ["UI.ConfirmDelete"] = "CONFIRM PRODUCT DELETION",
        ["UI.ConfirmSell"] = "CONFIRM SELL ITEM",
        ["UI.Transfer.Title"] = "TRANSFER RP TO PLAYER",
        ["UI.Transfer.Confirm"] = "CONFIRM ({0}RP)",
        ["UI.Transfer.Success"] = "TRANSFER COMPLETE!",
        ["UI.Transfer.Sent"] = "You have transferred {0}RP to {1}",
        ["UI.Exchange.Title"] = "EXCHANGE RP",
        ["UI.Exchange.Confirm"] = "CONFIRM ({0}RP)",
        ["UI.Exchange.Convert"] = "CONVERT",
        ["UI.Exchange.RP"] = "RP",
        ["UI.Exchange.Economics"] = "EC",
        ["UI.Exchange.Rate"] = "EXCHANGE RATE: 1RP - TO - {0} ECONOMICS",
        ["UI.Exchange.Balances"] = "{0}RP | {1}EC",
        ["UI.Exchange.RPToEconomics"] = "TO {0} ECONOMICS",
        ["UI.Exchange.EconomicsToRP"] = "TO {0} RP",
        ["UI.Exchange.Error.Balance.RP"] = "You do not have the required RP to make this exchange",
        ["UI.Exchange.Error.Balance.Economics"] = "You do not have the required Economics to make this exchange",
        ["UI.Exchange.Error.NoEconomics"] = "Economics is not currently loaded on the server",
        ["UI.Exchange.Error.Economics.DepositFailed"] = "Economics deposit failed...",
        ["UI.Exchange.Error.Economics.WithdrawFailed"] = "Economics withdrawal failed...",
        ["UI.Exchange.Success"] = "EXCHANGE COMPLETE!",
        ["UI.Exchange.Success.RPToEconomics"] = "You have exchanged {0}RP for {1} Economics",
        ["UI.Exchange.Success.EconomicsToRP"] = "You have exchanged {0} Economics for {1}RP",
        ["UI.User"] = "USER",
        ["UI.Free"] = "FREE",
        ["UI.Cost"] = "{0}RP",
        ["UI.DLCItem"] = "REQUIRES DLC",
        ["UI.Edit"] = "EDIT",
        ["UI.Delete"] = "DEL",
        ["UI.UnitPrice"] = "UNIT PRICE",
        ["UI.Amount"] = "AMOUNT",
        ["UI.Total"] = "TOTAL",
        ["UI.ConfirmSale"] = "CONFIRM (+{0}RP)",
        ["UI.Sold.Title"] = "ITEM SOLD!",
        ["UI.Sold.Message"] = "You sold {0}x {1} for {2}RP",
        ["UI.Error.Title"] = "ERROR!",
        ["UI.Error.InvalidProduct"] = "The product you are trying to purchase does not exist",
        ["UI.Error.GivePurchase"] = "A error occured when purchasing this product...",
        ["UI.Error.InsufficientBalance"] = "You do not have the required funds to make this purchase",
        ["UI.Error.OnCooldown"] = "You are still on cooldown for this product",
        ["UI.Purchase.Title"] = "PRODUCT PURCHASED!",
        ["UI.Purchase.Success"] = "You have purchased {0} for {1}RP",
        ["UI.Purchase.Free"] = "You have claimed {0}",
        ["UI.Error.ZeroTransferAmount"] = "You have not set a valid transfer amount",
        ["UI.Error.InsufficientTransferBalance"] = "You do not have the required funds to make this transfer",
        ["UI.Error.NoTransferTarget"] = "You have not set a valid transfer target",
        ["Message.Transfer.Received"] = "You have received {0}RP from {1}",
        ["Message.Error.NoCategories"] = "Nothing available for this store",
        ["Message.Title"] = "[<color=#B6F34A>Server Rewards</color>]",
        ["Message.Notification.Unspent"] = "You currently have <color=#B6F34A>{0}RP</color> to spend.",
        ["Message.Notification.Unspent.Command"] = "You can access the store by typing <color=#B6F34A>/{0}</color>",
        ["Message.Notification.Unspent.NPC"] = "Find a <color=#B6F34A>NPC dealer</color> to access the store",
        ["Message.ShopBlockedByPlugin"] = "Another plugin is preventing you from accessing the store at this time"
    };
    
    #endregion
}