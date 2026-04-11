using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.GameData.Characters;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Quests;
using System;
using System.Collections.Generic;

namespace PostHelpWantedAds
{
    internal sealed class ModEntry : Mod
    {
        private ModData _moddedData;
        private EscapableNamingMenu _namingMenu = null;
        private bool _hasShownBillboardMessage = false;
        private bool _hasTriggeredMorningEvent = false;
        private int _requiredGold = 0;
        private bool _deliveryAccepted = false;
        Random _random = new Random();

        public override void Entry(IModHelper helper)
        {
            Helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            Helper.Events.GameLoop.DayStarted += OnDayStarted;
            Helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            Helper.Events.Input.ButtonPressed += OnButtonPressed;
            Helper.Events.Display.MenuChanged += OnMenuChanged;
            Helper.Events.Content.AssetRequested += OnAssetRequested;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            _moddedData = Helper.Data.ReadSaveData<ModData>("PostHelpWantedAds") ?? new ModData();

            Monitor.Log($"[DayStarted] ModData State — " +
            $"DidPostAd: {_moddedData.DidPostAd}, " +
            $"PostedItem: {_moddedData.PostedItem}, " +
            $"ActiveQuestId: {_moddedData.ActiveQuestId}, " +
            $"ActiveQuestData: {_moddedData.ActiveQuestData}",
            LogLevel.Debug);

            if (_moddedData.DidPostAd && string.IsNullOrEmpty(_moddedData.ActiveQuestData))
            {
                //If quest data is missing or empty, reset all posting state to defaults
                cancelAd();
                Helper.Data.WriteSaveData("PostHelpWantedAds", _moddedData);

                Monitor.Log("Quest data missing; reset _moddedData to wipe slate", LogLevel.Warn);
                Game1.chatBox.addMessage("Uh-oh, a gust of wind blew away the help wanted ad you posted!", Color.Red);
            }

            if (_moddedData.DidPostAd)
            {
                Helper.GameContent.InvalidateCache("Data/Quests");
            }
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            if (_moddedData.DidPostAd)
            {
                _moddedData.DaysSincePost += 1;
                if (_moddedData.DaysSincePost <= 2)
                {
                    if (!Game1.objectData.TryGetValue(_moddedData.ItemIdStr, out var itemData))
                    {
                        Monitor.Log($"Item ID not found for '{_moddedData.ItemIdStr}'. Cancelling ad.", LogLevel.Warn);
                        cancelAd();
                        return;
                    }

                    _requiredGold = Game1.objectData[_moddedData.ItemIdStr].Price * 3;
                    if (Game1.player.Money >= _requiredGold)
                    {
                        _moddedData.ChosenVillager = villagerSelection(_moddedData.ItemIdStr);
                        Helper.Data.WriteSaveData("PostHelpWantedAds", _moddedData);
                        if (_moddedData.ChosenVillager != null)
                        {
                            bool willDeliver = this.willDeliver(_moddedData.ChosenVillager);
                            if (willDeliver)
                            {
                                Helper.Events.Player.Warped += enableDelivery;
                            }
                            else
                            {
                                Monitor.Log($"Looks like {_moddedData.ChosenVillager} won't get to your request today.", LogLevel.Info);
                            }
                        }
                        else
                        {
                            Monitor.Log("Something went wrong upon villager selection.", LogLevel.Warn);
                        }
                    }
                    else
                    {
                        Monitor.Log("You don't have enough gold to accept a delivery.", LogLevel.Info);
                    }
                }
                else
                {
                    //Cancel ad 2 days after posting it
                    cancelAd();
                }
            }
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
            {
                return;
            }

            if (_moddedData.DidPostAd && !Game1.player.hasQuest("PostHelpWantedAds.PostedAd"))
            {
                cancelAd();
                Monitor.Log($"Error when adding quest. Cancelling ad.", LogLevel.Warn);
            }

            if (_hasTriggeredMorningEvent && Game1.CurrentEvent == null && !_deliveryAccepted)
            {
                Game1.player.addItemByMenuIfNecessary(new StardewValley.Object(_moddedData.ItemIdStr, 1));
                Game1.player.removeQuest(_moddedData.ActiveQuestId);
                Game1.player.Money -= _requiredGold;
                cancelAd();
                _deliveryAccepted = true;
            }
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {           
            if (!Context.IsWorldReady || e.Button != SButton.P) // P for “Post Ad”
                return;

            // Only activate while Billboard > dailyQuestBoard is open
            if (Game1.activeClickableMenu == null || Game1.activeClickableMenu.GetType().Name != "Billboard")
            {
                return;
            }

            bool isHelpWanted = (bool)Game1.activeClickableMenu.GetType()
                .GetField("dailyQuestBoard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(Game1.activeClickableMenu);

            if (!isHelpWanted)
            {
                return;
            }

            // Bring up menu for user input
            if (_moddedData.DidPostAd == false)
            {
                _namingMenu = new EscapableNamingMenu(
                    naming =>
                    {
                        _moddedData.PostedItem = naming;
                        
                        string itemIdString = getItemId(_moddedData.PostedItem);

                        if (!string.IsNullOrEmpty(itemIdString))
                        {
                            bool isAvailable = isAvailableForQuests(itemIdString);
                            if (!isAvailable)
                            {
                                _namingMenu.textBox.Text = "";
                            }
                            else
                            {
                                bool inSeason = isInSeason(itemIdString);
                                if (!inSeason)
                                {
                                    _namingMenu.textBox.Text = "";
                                }

                                if (inSeason)
                                {
                                    _moddedData.ItemIdStr = itemIdString;
                                    
                                    Game1.chatBox.addMessage("Ad posted for " + _moddedData.PostedItem! + ". Be sure to have enough gold for a delivery.", Color.White);

                                    //Add jounral entry for quest
                                    string questData = $"Basic/Help Wanted Ad Posted/I've posted an ad for a {_moddedData.PostedItem}. The more friends I have in town, the more likely someone is to respond, I'd say!/Have enough money for a {_moddedData.PostedItem}/0/-1/0//true";
                                    _moddedData.ActiveQuestData = questData;
                                    _moddedData.ActiveQuestId = "PostHelpWantedAds.PostedAd";
                                    _moddedData.DidPostAd = true;
                                    Helper.GameContent.InvalidateCache("Data/Quests");
                                    Game1.player.addQuest("PostHelpWantedAds.PostedAd");
                                    Helper.Data.WriteSaveData("PostHelpWantedAds", _moddedData);

                                    Game1.exitActiveMenu();
                                }
                            }
                        }
                        else
                        {
                            Game1.addHUDMessage(new HUDMessage("Invalid input entered! Try again", HUDMessage.error_type));
                            _namingMenu.textBox.Text = "";
                        }
                    },
                    "Enter desired item:",
                    () => { });

                Game1.activeClickableMenu = _namingMenu;
            }
            else
            {
                Game1.chatBox.addMessage("You've already posted an ad! Give villagers at least 2 days to respond.", Color.Red);
            }
        }

        private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo("Data/Quests"))
            {
                e.Edit(asset =>
                {
                    if (_moddedData == null || !_moddedData.DidPostAd || string.IsNullOrEmpty(_moddedData.ActiveQuestData)) 
                    {
                        return;
                    }

                    var data = asset.AsDictionary<string, string>().Data;
                    data["PostHelpWantedAds.PostedAd"] = _moddedData.ActiveQuestData;
                });
            }
        }

        private void OnMenuChanged(object sender, MenuChangedEventArgs e)
        {
            //Adds instructions on the bulletin board for how to post an ad
            if (e.NewMenu?.GetType().Name != "Billboard")
            {
                _hasShownBillboardMessage = false;
                return;
            }

            bool isHelpWanted = (bool)Game1.activeClickableMenu.GetType()
                .GetField("dailyQuestBoard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(Game1.activeClickableMenu);

            if (isHelpWanted && !_hasShownBillboardMessage)
            {
                Game1.chatBox.addMessage("Press P to post your own \"help wanted\" ad!", Color.Green);
                _hasShownBillboardMessage = true;
            }
        }

        private string getItemId(string playerInput)
        {
            Dictionary<string, string> nameItemLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Game1.objectData)
            {
                nameItemLookup[pair.Value.Name] = pair.Key;
            }

            try
            {
                string itemIdString = nameItemLookup[playerInput];
                _moddedData.PostedItem = Game1.objectData[itemIdString].Name;

                Monitor.Log($"Requested: {_moddedData.PostedItem}", LogLevel.Warn);

                return itemIdString;
            }
            catch
            {
                string itemIdString = "203"; // lol Strange Bun
                return itemIdString;
            }
        }

        private bool isAvailableForQuests(string itemId)
        {
            bool isAvailable = false;
            //string itemIdString = itemId.ToString();

            // Check for typical items
            Dictionary<string, string> eligibleDefaultItems = new Dictionary<string, string>
            {
                // FORAGE

                { "16",  "Wild Horseradish" },{ "18",  "Daffodil" },{ "20",  "Leek" },{ "22",  "Dandelion" },
                { "399", "Spring Onion" }, { "396", "Spice Berry" },{ "398", "Grape" },
                { "402", "Sweet Pea" },{ "404", "Common Mushroom" },{ "406", "Wild Plum" },{ "408", "Hazelnut" },
                { "412", "Winter Root" },{ "414", "Crystal Fruit" },{ "416", "Snow Yam" },{ "418", "Crocus" },
                { "78",  "Cave Carrot" },{ "283", "Holly"},{ "392", "Nautilus Shell"},{ "393", "Coral"},
                { "394", "Sea Urchin"},{ "397", "Rainbow Shell"},{ "153", "Green Algae"},{ "152", "Seaweed"},
                { "157", "White Algae"},{ "410", "Blackberry"},{ "296", "Salmonberry"},

                // CROPS (no fruit tree fruits)
 
                { "24",  "Parsnip" },{ "188", "Green Bean" },{ "190", "Cauliflower" },{ "192", "Potato" },
                { "250", "Kale" },{ "591", "Tulip" },{ "597", "Blue Jazz" },{ "254", "Melon" },
                { "256", "Tomato" },{ "258", "Hot Pepper" },{ "260", "Blueberry" },{ "270", "Corn" },
                { "304", "Hops" },{ "421", "Sunflower" },{ "593", "Summer Spangle" },{ "376", "Poppy" },
                { "272", "Eggplant" },{ "278", "Bok Choy" },{ "280", "Yam" },{ "282", "Cranberries" },
                { "300", "Amaranth" },{ "302", "Grape" },{ "595", "Fairy Rose" },{ "264", "Radish" },
                { "276", "Pumpkin" },{ "262", "Wheat" },

                // OTHERS
                { "174", "Egg" },{ "176", "Large Egg" },{ "184", "Milk" },{ "186", "Large Milk" },
                { "436", "Goat Milk" },{ "438", "L. Goat Milk" },{ "330", "Clay" },{ "723", "Oyster" },
                { "719", "Mussel" },{ "718", "Cockle" }, { "167", "Joja Cola"}
            };

            if (eligibleDefaultItems.ContainsKey(itemId))
            {
                isAvailable = true;
                return isAvailable;
            }
            else
            {
                // Check for items that must be unlocked before requesting
                Dictionary<string, string> eligibleShippedItems = new Dictionary<string, string>
                {
                    { "266", "Red Cabbage" },{ "965", "Carrot" },{ "967", "Summer Squash" },{ "969", "Broccoli" },
                    { "971", "Powdermelon" },{ "281", "Chanterelle" }, { "420", "Red Mushroom" },{ "422", "Purple Mushroom" },
                    { "88",  "Coconut" },{ "90",  "Cactus Fruit" },{ "193", "Garlic" },{ "252", "Rhubarb" },
                    { "268", "Starfruit" },{ "274", "Artichoke" },{ "284", "Beet" },{ "259", "Fiddlehead Fern"},
                    { "400", "Strawberry" },{ "271", "Unmilled Rice" },{ "830", "Taro Root" },{ "832", "Pineapple" },
                    { "851", "Magma Cap" },{ "829", "Ginger" },{ "815", "Tea Leaves" },{ "257", "Morel" },
                    { "787", "Battery Pack" },{ "338", "Refined Quartz" },{ "334", "Copper Bar" },{ "335", "Iron Bar" },
                    { "336", "Gold Bar" },{ "82", "Fire Quartz" },{ "80", "Quartz" },{ "72", "Diamond" },
                    { "70", "Jade" },{ "68", "Topaz" },{ "66", "Amethyst" },{ "64", "Ruby" },
                    { "62", "Aquamarine" },{ "60", "Emerald" },{ "86", "Earth Crystal" },{ "84", "Frozen Tear" }
                };

                if (eligibleShippedItems.ContainsKey(itemId))
                {
                    bool hasShipped = Game1.player.basicShipped.ContainsKey("(O)" + itemId);
                    bool hasEncountered = Game1.player.mineralsFound.ContainsKey("(O)" + itemId);

                    if (hasShipped || hasEncountered)
                    {
                        isAvailable = true;
                        return isAvailable;
                    }
                    else
                    {
                        Game1.addHUDMessage(new HUDMessage("You haven't encountered/shipped that item yet", HUDMessage.error_type));
                    }
                }
                else
                {
                    // Check for fish that have been caught
                    Dictionary<string, string> eligibleFish = new Dictionary<string, string>
                    {
                        { "128", "Pufferfish" },{ "129", "Anchovy" },{ "130", "Tuna" },{ "131", "Sardine" },
                        { "132", "Bream" },{ "136", "Largemouth Bass" },{ "137", "Smallmouth Bass" },{ "138", "Rainbow Trout" },
                        { "140", "Walleye" },{ "142", "Carp" },{ "143", "Catfish" },{ "144", "Pike" },
                        { "145", "Sunfish" },{ "146", "Red Mullet" },{ "147", "Herring" },{ "148", "Eel" },
                        { "149", "Octopus" },{ "150", "Red Snapper" },{ "151", "Squid" },{ "154", "Sea Cucumber" },
                        { "155", "Super Cucumber" },{ "156", "Ghostfish" },{ "158", "Stonefish" },{ "161", "Ice Pip" },
                        { "162", "Lava Eel" },{ "164", "Sandfish" },{ "165", "Scorpion Carp" },{ "734", "Woodskip" },
                        { "698", "Sturgeon" },{ "699", "Tiger Trout" },{ "700", "Bullhead" },{ "701", "Tilapia" },
                        { "702", "Chub" },{ "704", "Dorado" },{ "705", "Albacore" },{ "706", "Shad" },
                        { "707", "Lingcod" },{ "708", "Halibut" },{ "798", "Midnight Carp" },{ "722", "Periwinkle" },
                        { "715", "Lobster" },{ "716", "Crayfish" },{ "717", "Crab" },{ "720", "Shrimp" },
                        { "721", "Snail" },{ "836", "Stingray" },{ "837", "Lionfish" },{ "838", "Blue Discus" },
                        { "267", "Flounder" },{ "139", "Salmon" },{ "141", "Perch" },{"Goby", "Goby"}
                    };

                    if (eligibleFish.ContainsKey(itemId))
                    {
                        bool hasCaught = Game1.player.fishCaught.ContainsKey("(O)" + itemId);
                        if (hasCaught)
                        {
                            isAvailable = true;
                            return isAvailable;
                        }
                        else
                        {
                            Game1.addHUDMessage(new HUDMessage("You haven't caught that fish yet", HUDMessage.error_type));
                        }
                    }
                    else
                    {
                        Game1.addHUDMessage(new HUDMessage("Sorry, that item isn't available", HUDMessage.error_type));
                    }
                }
            }
            return isAvailable;
        }

        private bool isInSeason(string itemId)
        {
            bool inSeason = true;

            Dictionary<string, string> alwaysInSeason = new Dictionary<string, string>
            {
                { "420", "Red Mushroom" },{ "422", "Purple Mushroom" },{ "88",  "Coconut" },{ "90",  "Cactus Fruit" },
                { "851", "Magma Cap" },{ "829", "Ginger" },{ "815", "Tea Leaves" },
                { "787", "Battery Pack" },{ "338", "Refined Quartz" },{ "334", "Copper Bar" },{ "335", "Iron Bar" },
                { "336", "Gold Bar" },{ "82", "Fire Quartz" },{ "80", "Quartz" },{ "72", "Diamond" },
                { "70", "Jade" },{ "68", "Topaz" },{ "66", "Amethyst" },{ "64", "Ruby" },
                { "62", "Aquamarine" },{ "60", "Emerald" },{ "78",  "Cave Carrot" },{ "393", "Coral" },
                { "394", "Sea Urchin" },{ "153", "Green Algae" },{ "152", "Seaweed" },{ "157", "White Algae" },
                { "174", "Egg" },{ "176", "Large Egg" },{ "184", "Milk" },{ "186", "Large Milk" },
                { "436", "Goat Milk" },{ "438", "L. Goat Milk" },{ "330", "Clay" },{ "723", "Oyster" },
                { "719", "Mussel" },{ "718", "Cockle" },{ "86", "Earth Crystal" },{ "84", "Frozen Tear" },
                { "132", "Bream" },{ "136", "Largemouth Bass" },{ "142", "Carp" },{ "167", "Joja Cola" },
                { "156", "Ghostfish" },{ "158", "Stonefish" },{ "161", "Ice Pip" },
                { "162", "Lava Eel" },{ "164", "Sandfish" },{ "165", "Scorpion Carp" },{ "734", "Woodskip" },
                { "700", "Bullhead" },{ "702", "Chub" },{ "722", "Periwinkle" },{"Goby", "Goby"},
                { "715", "Lobster" },{ "716", "Crayfish" },{ "717", "Crab" },{ "720", "Shrimp" },
                { "721", "Snail" },{ "836", "Stingray" },{ "837", "Lionfish" },{ "838", "Blue Discus" }
            };

            Dictionary<string, string> springSeasonals = new Dictionary<string, string>
            {
                { "16",  "Wild Horseradish" },{ "18",  "Daffodil" },{ "20",  "Leek" },{ "22",  "Dandelion" },
                { "257", "Morel" },{ "399", "Spring Onion" },{ "24",  "Parsnip" },{ "188", "Green Bean" },
                { "190", "Cauliflower" },{ "192", "Potato" },{ "250", "Kale" },{ "591", "Tulip" },
                { "597", "Blue Jazz" },{ "965", "Carrot" },{ "193", "Garlic" },{ "252", "Rhubarb" },
                { "400", "Strawberry" },{ "271", "Unmilled Rice" },{ "129", "Anchovy" },{ "131", "Sardine" },
                { "137", "Smallmouth Bass" },{ "143", "Catfish" },{ "145", "Sunfish" },{ "147", "Herring" },
                { "148", "Eel" },{ "706", "Shad" },{ "708", "Halibut" },{ "267", "Flounder" },{ "296", "Salmonberry" }
            };

            Dictionary<string, string> summerSeasonals = new Dictionary<string, string>
            {
                { "396", "Spice Berry" },{ "398", "Grape" },{ "402", "Sweet Pea" },{ "254", "Melon" },
                { "256", "Tomato" },{ "258", "Hot Pepper" },{ "260", "Blueberry" },{ "270", "Corn" },
                { "304", "Hops" },{ "421", "Sunflower" },{ "593", "Summer Spangle" },{ "376", "Poppy" },
                { "264", "Radish" },{ "262", "Wheat" },{ "266", "Red Cabbage" },{ "967", "Summer Squash" },
                { "268", "Starfruit" },{ "259", "Fiddlehead Fern" },{ "830", "Taro Root" },{ "832", "Pineapple" },
                { "128", "Pufferfish" },{ "130", "Tuna" },{ "138", "Rainbow Trout" },{ "397", "Rainbow Shell" },
                { "144", "Pike" },{ "145", "Sunfish" },{ "146", "Red Mullet" },{ "149", "Octopus" },
                { "150", "Red Snapper" },{ "155", "Super Cucumber" },{ "698", "Sturgeon" },{ "701", "Tilapia" },
                { "704", "Dorado" },{ "706", "Shad" },{ "708", "Halibut" },{ "267", "Flounder" }
            };

            Dictionary<string, string> fallSeasonals = new Dictionary<string, string>
            {
                { "404", "Common Mushroom" },{ "406", "Wild Plum" },{ "408", "Hazelnut" },{ "410", "Blackberry" },
                { "270", "Corn" },{ "421", "Sunflower" },{ "272", "Eggplant" },{ "278", "Bok Choy" },
                { "280", "Yam" },{ "282", "Cranberries" },{ "300", "Amaranth" },{ "302", "Grape" },
                { "595", "Fairy Rose" },{ "276", "Pumpkin" },{ "262", "Wheat" },{ "969", "Broccoli" },
                { "274", "Artichoke" },{ "284", "Beet" },{ "798", "Midnight Carp" },{ "139", "Salmon" },
                { "129", "Anchovy" },{ "131", "Sardine" },{ "137", "Smallmouth Bass" },{ "140", "Walleye" },
                { "143", "Catfish" },{ "148", "Eel" },{ "150", "Red Snapper" },{ "154", "Sea Cucumber" },
                { "155", "Super Cucumber" },{ "699", "Tiger Trout" },{ "701", "Tilapia" },{ "705", "Albacore" },
                { "706", "Shad" }
            };

            Dictionary<string, string> winterSeasonals = new Dictionary<string, string>
            {
                { "412", "Winter Root" },{ "414", "Crystal Fruit" },{ "416", "Snow Yam" },{ "418", "Crocus" },
                { "283", "Holly" },{ "392", "Nautilus Shell" },{ "971", "Powdermelon" },
                { "130", "Tuna" },{ "131", "Sardine" },{ "146", "Red Mullet" },{ "147", "Herring" },
                { "149", "Octopus" },{ "151", "Squid" },{ "154", "Sea Cucumber" },{ "155", "Super Cucumber" },
                { "141", "Perch" },{ "698", "Sturgeon" },{ "705", "Albacore" },{ "707", "Lingcod" },
                { "708", "Halibut" },{ "798", "Midnight Carp" }
            };

            if (alwaysInSeason.ContainsKey(itemId))
            {
                return inSeason;
            }

            if (Game1.season == Season.Spring)
            {
                if (springSeasonals.ContainsKey(itemId))
                {
                    return inSeason;
                }
                else
                {
                    Game1.addHUDMessage(new HUDMessage("That item isn't in season", HUDMessage.error_type));

                    inSeason = false;
                    return inSeason;
                }
            }

            if (Game1.season == Season.Summer)
            {
                if (summerSeasonals.ContainsKey(itemId))
                {
                    return inSeason;
                }
                else
                {
                    Game1.addHUDMessage(new HUDMessage("That item isn't in season", HUDMessage.error_type));

                    inSeason = false;
                    return inSeason;
                }
            }

            if (Game1.season == Season.Fall)
            {
                if (fallSeasonals.ContainsKey(itemId))
                {
                    return inSeason;
                }
                else
                {
                    Game1.addHUDMessage(new HUDMessage("That item isn't in season", HUDMessage.error_type));

                    inSeason = false;
                    return inSeason;
                }
            }
            if (Game1.season == Season.Winter)
            {
                if (winterSeasonals.ContainsKey(itemId))
                {
                    return inSeason;
                }
                else
                {
                    Game1.addHUDMessage(new HUDMessage("That item isn't in season", HUDMessage.error_type));

                    inSeason = false;
                    return inSeason;
                }
            }

            bool inAnyList = springSeasonals.ContainsKey(itemId) ||
                 summerSeasonals.ContainsKey(itemId) ||
                 fallSeasonals.ContainsKey(itemId) ||
                 winterSeasonals.ContainsKey(itemId);

            if (!inAnyList)
            {
                Monitor.Log($"Item {itemId} not found in any season list, defaulting to in-season.", LogLevel.Warn);                
            }

            return inSeason;
        }

        private string villagerSelection(string postedItem)
        {
            //Different villagers have different odds of delivering certain types of items!
            //(E.g. Willy often delivers fish, Caroline will offer vegetables from her garden)

            int itemCategory = Game1.objectData[postedItem].Category;

            if (itemCategory == StardewValley.Object.VegetableCategory || itemCategory == StardewValley.Object.FruitsCategory)
            {
                Dictionary<string, double> cropVillagers = new Dictionary<string, double>
                {
                    { "Harvey", 0.05 },{ "Caroline", 0.25 },{ "Pierre", 0.20 },{ "Gus", 0.15 },{ "Lewis", 0.1 },
                    { "Jodi", 0.20 },{ "Kent", 0.05 }
                };

                return rollVillager(cropVillagers);
            }

            else if (itemCategory == StardewValley.Object.flowersCategory)
            {
                Dictionary<string, double> flowerVillagers = new Dictionary<string, double>
                {
                    { "Evelyn", 0.4 },{ "Caroline", 0.2 },{ "Haley", 0.1 },{ "Jodi", 0.2 },{ "Vincent", 0.1 }
                };

                return rollVillager(flowerVillagers);
            }

            else if (itemCategory == StardewValley.Object.FishCategory)
            {
                Dictionary<string, double> fishVillagers = new Dictionary<string, double>
                {
                    { "Willy", 0.6 },{ "Elliot", 0.15 },{ "Pam", 0.05 },{ "Linus", 0.15 },{ "Leo", 0.05 }
                };

                return rollVillager(fishVillagers);
            }

            else if (itemCategory == StardewValley.Object.MilkCategory)
            {
                Dictionary<string, double> milkVillagers = new Dictionary<string, double>
                {
                    { "Alex", 0.04 },{ "Gus", 0.04 },{ "Lewis", 0.04 },{ "Sandy", 0.01 },{ "Sam", 0.03 },
                    { "Shane", 0.25 },{ "Jas", 0.09 },{ "Marnie", 0.5 }
                };

                return rollVillager(milkVillagers);
            }

            else if (itemCategory == StardewValley.Object.EggCategory)
            {
                Dictionary<string, double> eggVillagers = new Dictionary<string, double>
                {
                    { "Alex", 0.04 },{ "Gus", 0.04 },{ "Lewis", 0.04 },{ "Sam", 0.04 },{ "Shane", 0.25 },
                    { "Jas", 0.09 },{ "Marnie", 0.5 }
                };

                return rollVillager(eggVillagers);
            }

            else if (itemCategory == StardewValley.Object.junkCategory)
            {
                Dictionary<string, double> trashVillagers = new Dictionary<string, double>
                {
                    { "Willy", 0.05 },{ "Sam", 0.45 },{ "Shane", 0.5 }
                };

                return rollVillager(trashVillagers);
            }

            else if (itemCategory == StardewValley.Object.mineralsCategory)
            {
                Dictionary<string, double> mineralVillagers = new Dictionary<string, double>
                {
                    { "Abigail", 0.32 },{ "Clint", 0.32 },{ "Linus", 0.04 },{ "Sebastian", 0.32 }
                };

                return rollVillager(mineralVillagers);
            }

            else if (itemCategory == StardewValley.Object.GemCategory)
            {
                Dictionary<string, double> gemVillagers = new Dictionary<string, double>
                {
                    { "Abigail", 0.2 },{ "Clint", 0.2 },{ "Emily", 0.2 },{ "Sebastian", 0.2 },{ "Maru", 0.2 }
                };

                return rollVillager(gemVillagers);
            }

            else if (itemCategory == StardewValley.Object.GreensCategory)
            {
                Dictionary<string, double> forageVillagers = new Dictionary<string, double>
                {
                    { "Pierre", 0.05 },{ "Evelyn", 0.05 },{ "Penny", 0.05 },{ "Elliot", 0.05 },{ "Vincent", 0.05 },
                    { "Haley", 0.05 },{ "Leah", 0.3 },{ "Linus", 0.3 },{ "Leo", 0.05 },{ "Demetrius", 0.05 }
                };

                return rollVillager(forageVillagers);
            }

            else if (itemCategory == StardewValley.Object.metalResources)
            {
                Dictionary<string, double> metalResourceVillagers = new Dictionary<string, double>
                {
                    { "Clint", 0.7 },{ "Robin", 0.1 },{ "Maru", 0.15 },{ "Kent", 0.05 },
                };

                return rollVillager(metalResourceVillagers);
            }

            else if (itemCategory == StardewValley.Object.buildingResources)
            {
                Dictionary<string, double> buildingResourceVillagers = new Dictionary<string, double>
                {
                    { "Pam", 0.1 },{ "Penny", 0.1 },{ "Sam", 0.1 },{ "Vincent", 0.1 },{ "Kent", 0.1 },
                    { "Shane", 0.1 },{ "Jas", 0.1 },{ "Robin", 0.1 },{ "Maru", 0.1 },{ "Demetrius", 0.1 }
                };

                return rollVillager(buildingResourceVillagers);
            }

            else
            {
                //If item falls outside any of the stated categories, it's free game for most villagers
                Dictionary<string, double> defaultVillagers = new Dictionary<string, double>
                {
                    { "Harvey", 0.5 },{ "Caroline", 0.5 },{ "Pierre", 0.5 },{ "Abigail", 0.5 },{ "Evelyn", 0.5 },
                    { "Alex", 0.5 },{ "Gus", 0.5 },{ "Pam", 0.5 },{ "Penny", 0.5 },{ "Lewis", 0.5 },
                    { "Clint", 0.5 },{ "Elliot", 0.5 },{ "Willy", 0.5 },{ "Sam", 0.5 },{ "Vincent", 0.5 },
                    { "Jodi", 0.5 },{ "Kent", 0.5 },{ "Haley", 0.5 },{ "Emily", 0.5 },{ "Leah", 0.5 },
                    { "Shane", 0.5 },{ "Jas", 0.5 },{ "Marnie", 0.5 },{ "Linus", 0.5 },{ "Leo", 0.5 },
                    { "Sebastian", 0.5 },{ "Robin", 0.5 },{ "Maru", 0.5 },{ "Demetrius", 0.5 }
                };

                return rollVillager(defaultVillagers);
            }
            return "";
        }

        private bool willDeliver(string chosenVillager)
        {
            bool willDeliver = false;

            int friendshipPoints = Game1.player.friendshipData[chosenVillager].Points;
            //Always at least a 20% chance of delivery, no more than 85% chance
            double percentage = Math.Clamp((friendshipPoints / 2500.0) + Game1.player.DailyLuck, 0.20, 0.85);

            double roll = _random.NextDouble();
            if (roll < percentage)
            {
                willDeliver = true;
            }

            return willDeliver;
        }

        private void enableDelivery(object sender, WarpedEventArgs e)
        {
            if (e.NewLocation is Farm && e.OldLocation is FarmHouse && !_hasTriggeredMorningEvent)
            {
                _hasTriggeredMorningEvent = true;
                Helper.Events.Player.Warped -= enableDelivery;

                string playerName = Game1.player.Name;
                string eventScript = $"none/64 15/farmer 64 16 2 {_moddedData.ChosenVillager} 64 18 0/" +
                    $"pause 1500/" +
                    $"speak {_moddedData.ChosenVillager} \"$h Hey {playerName}!\"/" +
                    $"speak {_moddedData.ChosenVillager} \"I saw on the bulletin board that you were looking for a {_moddedData.PostedItem}. I just so happened to have one, too!\"/" +
                    $"speak {_moddedData.ChosenVillager} \"$h Here you go!\"/" +
                    $"pause 250/" +
                    $"playSound give_gift/" +
                    $"pause 500/" +
                    $"pause 500/" +
                    $"emote farmer 20/" +
                    $"pause 1000/" +
                    $"speak {_moddedData.ChosenVillager} \"$h Glad I could help. Enjoy your {_moddedData.PostedItem}!\"/" +
                    $"end";
                Game1.currentLocation.startEvent(new StardewValley.Event(eventScript));
                
            }
        }

        private void cancelAd()
        {
            _moddedData.DidPostAd = false;
            _moddedData.PostedItem = "";
            _moddedData.ItemIdStr = "";
            _moddedData.ChosenVillager = "";
            _moddedData.DaysSincePost = 0;
            _moddedData.ActiveQuestData = "";
            _moddedData.ActiveQuestId = "";
            _hasTriggeredMorningEvent = false;
            _deliveryAccepted = false;
            Game1.player.removeQuest("PostHelpWantedAds.PostedAd");
            Helper.Data.WriteSaveData("PostHelpWantedAds", _moddedData);
        }

        private string rollVillager(Dictionary<string, double> villagers)
        {
            //Excluded unmet villagers and player spouse
            var eligible = villagers
                .Where(kvp => Game1.player.friendshipData.ContainsKey(kvp.Key) && !Game1.player.friendshipData[kvp.Key].IsMarried())
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            double total = eligible.Values.Sum();
            double roll = _random.NextDouble() * total;
            double cumulative = 0.0;

            foreach (var kvp in eligible)
            {
                cumulative += kvp.Value;
                if (roll < cumulative)
                    return kvp.Key;
            }
            return "";
        }
    }
}