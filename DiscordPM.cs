using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Ext.Discord.Attributes;
using Oxide.Ext.Discord.Builders;
using Oxide.Ext.Discord.Cache;
using Oxide.Ext.Discord.Clients;
using Oxide.Ext.Discord.Connections;
using Oxide.Ext.Discord.Constants;
using Oxide.Ext.Discord.Entities;
using Oxide.Ext.Discord.Extensions;
using Oxide.Ext.Discord.Interfaces;
using Oxide.Ext.Discord.Libraries;
using Oxide.Ext.Discord.Logging;
using Oxide.Ext.Discord.Types;

#if RUST
using UnityEngine;
#endif

namespace Oxide.Plugins
{
    // ReSharper disable once UnusedType.Global
    [Info("Discord PM", "MJSU", "3.0.0")]
    [Description("Allows private messaging through discord")]
    internal class DiscordPM : CovalencePlugin, IDiscordPlugin, IDiscordPool
    {
        #region Class Fields
        public DiscordClient Client { get; set; }
        public DiscordPluginPool Pool { get; set; }
        
        private PluginConfig _pluginConfig;

        private const string AccentColor = "de8732";
        private const string PmCommand = "pm";
        private const string ReplyCommand = "r";
        private const string NameArg = "name";
        private const string MessageArg = "message";

        private readonly DiscordPlaceholders _placeholders = GetLibrary<DiscordPlaceholders>();
        private readonly DiscordMessageTemplates _templates = GetLibrary<DiscordMessageTemplates>();
        private readonly DiscordCommandLocalizations _localizations = GetLibrary<DiscordCommandLocalizations>();

        private readonly Hash<string, IPlayer> _replies = new();

        private readonly BotConnection  _discordSettings = new()
        {
            Intents = GatewayIntents.Guilds
        };

        private DiscordChannel _logChannel;
        private DiscordApplicationCommand _pmCommand;
        
#if RUST
        private Effect _effect;
#endif
        #endregion

        #region Setup & Loading
        // ReSharper disable once UnusedMember.Local
        private void Init()
        {
            RegisterServerLangCommand(nameof(DiscordPmChatCommand), LangKeys.ChatPmCommand);
            RegisterServerLangCommand(nameof(DiscordPmChatReplyCommand), LangKeys.ChatReplyCommand);

            _discordSettings.ApiToken = _pluginConfig.DiscordApiKey;
            _discordSettings.LogLevel = _pluginConfig.ExtensionDebugging;
        }

        [HookMethod(DiscordExtHooks.OnDiscordClientCreated)]
        private void OnDiscordClientCreated()
        {
            Client.Connect(_discordSettings);
                
            RegisterPlaceholders();
            RegisterGlobalTemplates();
            RegisterEnTemplates();
            RegisterRuTemplates();
        }
        
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [LangKeys.Chat] = $"[#BEBEBE][[#{AccentColor}]{Title}[/#]] {PlaceholderKeys.Chat}[/#]",
                [LangKeys.ToFormat] = $"[#BEBEBE][#{AccentColor}]PM to {DefaultKeys.PlayerTarget.NameClan}:[/#] {PlaceholderKeys.Message}[/#]",
                [LangKeys.FromFormat] = $"[#BEBEBE][#{AccentColor}]PM from {DefaultKeys.Player.NameClan}:[/#] {PlaceholderKeys.Message}[/#]",
                [LangKeys.LogFormat] = $"{DefaultKeys.Player.NameClan} -> {DefaultKeys.PlayerTarget.NameClan}: {PlaceholderKeys.Message}",
                
                [LangKeys.InvalidPmSyntax] = $"Invalid Syntax. Type [#{AccentColor}]/{DefaultKeys.Plugin.Lang.WithFormat(LangKeys.ChatPmCommand)} MJSU Hi![/#]",
                [LangKeys.InvalidReplySyntax] = $"Invalid Syntax. Ex: [#{AccentColor}]/{DefaultKeys.Plugin.Lang.WithFormat(LangKeys.ChatReplyCommand)} Hi![/#]",
                [LangKeys.NoPreviousPm] = $"You do not have any previous discord PM's. Please use /{DefaultKeys.Plugin.Lang.WithFormat(LangKeys.ChatPmCommand)} to be able to use this command.",
                [LangKeys.NoPlayersFound] = $"No players found with the name '{PlaceholderKeys.NotFound}'",
                [LangKeys.MultiplePlayersFound] = $"Multiple players found with the name '{PlaceholderKeys.NotFound}'.",

                [LangKeys.ChatPmCommand] = "pm",
                [LangKeys.ChatReplyCommand] = "r",
            }, this);
            
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [LangKeys.Chat] = $"[#BEBEBE][[#{AccentColor}]{Title}[/#]] {PlaceholderKeys.Chat}[/#]",
                [LangKeys.ToFormat] = $"[#BEBEBE][#{AccentColor}]ЛС для {DefaultKeys.PlayerTarget.NameClan}:[/#] {PlaceholderKeys.Message}[/#]",
                [LangKeys.FromFormat] = $"[#BEBEBE][#{AccentColor}]ЛС от {DefaultKeys.Player.NameClan}:[/#] {PlaceholderKeys.Message}[/#]",
                [LangKeys.LogFormat] = $"{DefaultKeys.Player.NameClan} -> {DefaultKeys.PlayerTarget.NameClan}: {PlaceholderKeys.Message}",
                
                [LangKeys.InvalidPmSyntax] = $"Недопустимый синтаксис. Введите [#{AccentColor}]/{DefaultKeys.Plugin.Lang.WithFormat(LangKeys.ChatPmCommand)} MJSU, привет![/#]",
                [LangKeys.InvalidReplySyntax] = $"Недопустимый синтаксис. Пример: [#{AccentColor}]/{DefaultKeys.Plugin.Lang.WithFormat(LangKeys.ChatReplyCommand)} Привет![/#]",
                [LangKeys.NoPreviousPm] = $"У вас нет предыдущих личных сообщений. Пожалуйста, используйте /{DefaultKeys.Plugin.Lang.WithFormat(LangKeys.ChatPmCommand)} чтобы иметь возможность использовать эту команду.",
                [LangKeys.NoPlayersFound] = $"Игроки с именем '{PlaceholderKeys.NotFound}' не найдены",
                [LangKeys.MultiplePlayersFound] = $"Найдено несколько игроков с именем '{PlaceholderKeys.NotFound}'.",

                [LangKeys.ChatPmCommand] = "pm",
                [LangKeys.ChatReplyCommand] = "r",
            }, this, "ru");
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Loading Default Config");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            Config.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
            _pluginConfig = AdditionalConfig(Config.ReadObject<PluginConfig>());
            Config.WriteObject(_pluginConfig);
        }

        private PluginConfig AdditionalConfig(PluginConfig config)
        {
            config.Log = new LogSettings(config.Log);
            return config;
        }

        // ReSharper disable once UnusedMember.Local
        private void OnServerInitialized()
        {
            if (string.IsNullOrEmpty(_pluginConfig.DiscordApiKey))
            {
                PrintWarning("Please set the Discord Bot Token and reload the plugin");
                return;
            }

#if RUST
            if (_pluginConfig.EnableEffectNotification)
            {
                _effect = new Effect(_pluginConfig.EffectNotification, Vector3.zero, Vector3.zero);
                _effect.attached = true;
            }
#endif
        }
        #endregion

        #region Chat Commands
        // ReSharper disable once UnusedParameter.Local
        private void DiscordPmChatCommand(IPlayer player, string cmd, string[] args)
        {
            if (args.Length < 2)
            {
                Chat(player, LangKeys.InvalidPmSyntax, GetDefault());
                return;
            }

            IPlayer target;
            if (!TryFindPlayer(player, args[0], out target))
            {
                return;
            }

            _replies[player.Id] = target;
            _replies[target.Id] = player;

            string message = args.Length == 2 ? args[1] : string.Join(" ", args.Skip(1).ToArray());
            
            SendPrivateMessageFromServer(player, target, message);
        }

        // ReSharper disable once UnusedParameter.Local
        private void DiscordPmChatReplyCommand(IPlayer player, string cmd, string[] args)
        {
            if (args.Length < 1)
            {
                Chat(player, LangKeys.InvalidReplySyntax, GetDefault());
                return;
            }

            IPlayer target = _replies[player.Id];
            if (target == null)
            {
                Chat(player, LangKeys.NoPreviousPm, GetDefault());
                return;
            }
            
            string message = args.Length == 1 ? args[0] : string.Join(" ", args);
            SendPrivateMessageFromServer(player, target, message);
        }
        
        public void SendPrivateMessageFromServer(IPlayer sender, IPlayer target, string message)
        {
            using PlaceholderData data = GetPmDefault(sender, target, message);
            data.ManualPool();
            SendPlayerPrivateMessage(sender, LangKeys.ToFormat, TemplateKeys.Messages.To, data);
            SendPlayerPrivateMessage(target, LangKeys.FromFormat, TemplateKeys.Messages.From, data);
            LogPrivateMessage(data);
        }
        #endregion

        #region Discord Setup
        // ReSharper disable once UnusedMember.Local
        [HookMethod(DiscordExtHooks.OnDiscordGatewayReady)]
        private void OnDiscordGatewayReady()
        {
            RegisterApplicationCommands();
            Puts($"{Title} Ready");
        }

        // ReSharper disable once UnusedMember.Local
        [HookMethod(DiscordExtHooks.OnDiscordGuildCreated)]
        private void OnDiscordGuildCreated(DiscordGuild guild)
        {
            _logChannel = guild.Channels[_pluginConfig.Log.LogToChannelId];
        }

        public void RegisterApplicationCommands()
        {
            CreatePmCommand();
            CreateReplyCommand();
        }

        public void CreatePmCommand()
        {
            ApplicationCommandBuilder pmCommand = new ApplicationCommandBuilder(PmCommand, "Private message a player", ApplicationCommandType.ChatInput)
                                                  .AddDefaultPermissions(PermissionFlags.None)
                                                  .AllowInDirectMessages(_pluginConfig.AllowInDm);
            AddCommandNameOption(pmCommand);
            AddCommandMessageOption(pmCommand);

            CommandCreate cmd = pmCommand.Build();
            DiscordCommandLocalization localization = pmCommand.BuildCommandLocalization();

            TemplateKey template = new("PM.Command");
            _localizations.RegisterCommandLocalizationAsync(this, template, localization, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0, 0)).Then(_ =>
            {
                _localizations.ApplyCommandLocalizationsAsync(this, cmd, template).Then(() =>
                {
                    Client.Bot.Application.CreateGlobalCommand(Client, cmd).Then(c => _pmCommand = c);
                });
            });
        }

        public void CreateReplyCommand()
        {
            ApplicationCommandBuilder replyCommand = new ApplicationCommandBuilder(ReplyCommand, "Reply to the last received private message", ApplicationCommandType.ChatInput)
                                                     .AddDefaultPermissions(PermissionFlags.None)
                                                     .AllowInDirectMessages(_pluginConfig.AllowInDm);
            AddCommandMessageOption(replyCommand);

            CommandCreate cmd = replyCommand.Build();
            DiscordCommandLocalization localization = replyCommand.BuildCommandLocalization();

            TemplateKey template = new("Reply.Command");
            _localizations.RegisterCommandLocalizationAsync(this, template, localization, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0, 0)).Then(_ =>
            {
                _localizations.ApplyCommandLocalizationsAsync(this, cmd, template).Then(() =>
                {
                    Client.Bot.Application.CreateGlobalCommand(Client, cmd);
                });
            });
        }

        public void AddCommandNameOption(ApplicationCommandBuilder builder)
        {
            builder.AddOption(CommandOptionType.String, NameArg, "Name of the player",
                options => options.Required().AutoComplete());
        }
        
        public void AddCommandMessageOption(ApplicationCommandBuilder builder)
        {
            builder.AddOption(CommandOptionType.String, MessageArg, "Message to send the player",
                options => options.Required());
        }
        #endregion

        #region Discord Commands
        // ReSharper disable once UnusedMember.Local
        [DiscordApplicationCommand(PmCommand)]
        private void HandlePmCommand(DiscordInteraction interaction, InteractionDataParsed parsed)
        {
            IPlayer player = interaction.User.Player;
            if (player == null)
            {
                interaction.CreateTemplateResponse(Client, InteractionResponseType.ChannelMessageWithSource, TemplateKeys.Errors.UnlinkedUser, GetInteractionCallback(interaction), GetDefault());
                return;
            }

            string targetId = parsed.Args.GetString(NameArg);
            string message = parsed.Args.GetString(MessageArg);

            IPlayer target = players.FindPlayerById(targetId);
            if (target == null)
            {
                interaction.CreateTemplateResponse(Client, InteractionResponseType.ChannelMessageWithSource, TemplateKeys.Errors.InvalidAutoCompleteSelection, GetInteractionCallback(interaction), GetDefault());
                return;
            }

            _replies[player.Id] = target;
            _replies[target.Id] = player;

            SendPrivateMessageFromDiscord(interaction, player, target, message);
        }
        
        // ReSharper disable once UnusedMember.Local
        [DiscordApplicationCommand(ReplyCommand)]
        private void HandleReplyCommand(DiscordInteraction interaction, InteractionDataParsed parsed)
        {
            IPlayer player = interaction.User.Player;
            if (player == null)
            {
                interaction.CreateTemplateResponse(Client, InteractionResponseType.ChannelMessageWithSource, TemplateKeys.Errors.UnlinkedUser, GetInteractionCallback(interaction), GetDefault());
                return;
            }
            
            string message = parsed.Args.GetString(MessageArg);

            IPlayer target = _replies[player.Id];
            if (target == null)
            {
                interaction.CreateTemplateResponse(Client, InteractionResponseType.ChannelMessageWithSource, TemplateKeys.Errors.NoPreviousPm, GetInteractionCallback(interaction), GetDefault().AddCommand(_pmCommand));
                return;
            }
            
            _replies[target.Id] = player;
            
            SendPrivateMessageFromDiscord(interaction, player, target, message);
        }
        
        public void SendPrivateMessageFromDiscord(DiscordInteraction interaction, IPlayer player, IPlayer target, string message)
        {
            using PlaceholderData data = GetPmDefault(player, target, message);
            data.ManualPool();
            if (!interaction.GuildId.HasValue)
            {
                ServerPrivateMessage(player, LangKeys.ToFormat, data);
            }
            else
            {
                SendPlayerPrivateMessage(player, LangKeys.ToFormat, TemplateKeys.Messages.To, data);
            }
            interaction.CreateTemplateResponse(Client, InteractionResponseType.ChannelMessageWithSource, TemplateKeys.Messages.To, GetInteractionCallback(interaction), GetPmDefault(player, target, message));
            SendPlayerPrivateMessage(target, LangKeys.FromFormat, TemplateKeys.Messages.From, data);
            LogPrivateMessage(data);
        }

        // ReSharper disable once UnusedMember.Local
        [DiscordAutoCompleteCommand(PmCommand, NameArg)]
        private void HandleNameAutoComplete(DiscordInteraction interaction, InteractionDataOption focused)
        {
            string search = focused.GetString();
            InteractionAutoCompleteBuilder response = interaction.GetAutoCompleteBuilder();
            response.AddAllOnlineFirstPlayers(search, PlayerNameFormatter.ClanName);
            interaction.CreateResponse(Client, response);
        }

        public InteractionCallbackData GetInteractionCallback(DiscordInteraction interaction)
        {
            return new InteractionCallbackData
            {
                Flags = interaction.GuildId.HasValue ? MessageFlags.Ephemeral : MessageFlags.None
            };
        }
        #endregion

        #region Discord Placeholders
        public void RegisterPlaceholders()
        {
            _placeholders.RegisterPlaceholder<string>(this, PlaceholderKeys.Message,  PlaceholderDataKeys.Message);
            _placeholders.RegisterPlaceholder<string>(this, PlaceholderKeys.NotFound,  PlaceholderDataKeys.PlayerName);
            _placeholders.RegisterPlaceholder<string>(this, PlaceholderKeys.Chat,  PlaceholderDataKeys.Chat);
        }

        public PlaceholderData GetPmDefault(IPlayer from, IPlayer to, string message)
        {
            return GetDefault()
                   .AddPlayer(from)
                   .AddTarget(to)
                   .Add(PlaceholderDataKeys.Message, message);
        }
        
        public PlaceholderData GetDefault()
        {
            return _placeholders.CreateData(this);
        }
        #endregion

        #region Discord Templates
        public void RegisterGlobalTemplates()
        {
            DiscordMessageTemplate logMessage = CreateTemplateEmbed($"[{DefaultKeys.TimestampNow.ShortTime}] {DefaultKeys.Player.NameClan} -> {DefaultKeys.PlayerTarget.NameClan}: {PlaceholderKeys.Message}", DiscordColor.Danger);
            _templates.RegisterGlobalTemplateAsync(this, TemplateKeys.Messages.Log, logMessage, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
        }
        
        public void RegisterEnTemplates()
        {
            DiscordMessageTemplate toMessage = CreateTemplateEmbed($"[{DefaultKeys.TimestampNow.ShortTime}] PM to {DefaultKeys.PlayerTarget.NameClan}: {PlaceholderKeys.Message}", DiscordColor.Success);
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Messages.To, toMessage, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
            
            DiscordMessageTemplate fromMessage = CreateTemplateEmbed($"[{DefaultKeys.TimestampNow.ShortTime}] PM from {DefaultKeys.Player.NameClan}: {PlaceholderKeys.Message}", DiscordColor.Danger);
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Messages.From, fromMessage, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));       
            
            DiscordMessageTemplate errorUnlinkedUser = CreatePrefixedTemplateEmbed("You cannot use this command until you're have linked your game and discord accounts", DiscordColor.Danger);
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Errors.UnlinkedUser, errorUnlinkedUser, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
            
            DiscordMessageTemplate errorInvalidAutoComplete = CreatePrefixedTemplateEmbed("The name you have picked does not appear to be a valid auto complete value. Please make sure you select one of the auto complete options.", DiscordColor.Danger);
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Errors.InvalidAutoCompleteSelection, errorInvalidAutoComplete, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
            
            DiscordMessageTemplate errorNoPreviousPm = CreatePrefixedTemplateEmbed($"You do not have any previous discord PM's. Please use {DefaultKeys.AppCommand.Mention} to be able to use this command.", DiscordColor.Danger);
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Errors.NoPreviousPm, errorNoPreviousPm, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
        }

        public void RegisterRuTemplates()
        {
            DiscordMessageTemplate toMessage = CreateTemplateEmbed($"[{DefaultKeys.TimestampNow.ShortTime}] ЛС для {DefaultKeys.PlayerTarget.NameClan}: {PlaceholderKeys.Message}", DiscordColor.Success);
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Messages.To, toMessage, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0), "ru");
            
            DiscordMessageTemplate fromMessage = CreateTemplateEmbed($"[{DefaultKeys.TimestampNow.ShortTime}] ЛС от {DefaultKeys.Player.NameClan}: {PlaceholderKeys.Message}", DiscordColor.Danger);
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Messages.From, fromMessage, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0), "ru");       
            
            DiscordMessageTemplate errorUnlinkedUser = CreatePrefixedTemplateEmbed("Вы не можете использовать эту команду, пока не свяжете игровой аккаунт с аккаунтом Discord.", DiscordColor.Danger);
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Errors.UnlinkedUser, errorUnlinkedUser, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0), "ru");
            
            DiscordMessageTemplate errorInvalidAutoComplete = CreatePrefixedTemplateEmbed("Имя, которое вы выбрали, не является допустимым значением автозаполнения. Пожалуйста, убедитесь, что вы выбираете один из вариантов автозаполнения.", DiscordColor.Danger);
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Errors.InvalidAutoCompleteSelection, errorInvalidAutoComplete, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0), "ru");
            
            DiscordMessageTemplate errorNoPreviousPm = CreatePrefixedTemplateEmbed($"У вас нет предыдущих личных сообщений. Пожалуйста, используйте {DefaultKeys.AppCommand.Mention}, чтобы использовать эту команду.", DiscordColor.Danger);
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Errors.NoPreviousPm, errorNoPreviousPm, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0), "ru");
        }
        
        public DiscordMessageTemplate CreateTemplateEmbed(string description, DiscordColor color)
        {
            return new DiscordMessageTemplate
            {
                Embeds = new List<DiscordEmbedTemplate>
                {
                    new()
                    {
                        Description = description,
                        Color = color.ToHex()
                    }
                }
            };
        }
        
        public DiscordMessageTemplate CreatePrefixedTemplateEmbed(string description, DiscordColor color)
        {
            return new DiscordMessageTemplate
            {
                Embeds = new List<DiscordEmbedTemplate>
                {
                    new()
                    {
                        Description = $"[{DefaultKeys.Plugin.Title}] {description}",
                        Color = color.ToHex()
                    }
                }
            };
        }
        #endregion
        
        #region Helpers
        public bool TryFindPlayer(IPlayer from, string name, out IPlayer target)
        {
            List<IPlayer> foundPlayers = Pool.GetList<IPlayer>();
            List<IPlayer> activePlayers = Pool.GetList<IPlayer>();
            List<IPlayer> linkedPlayers = Pool.GetList<IPlayer>();
            
            try
            {
                foreach (IPlayer player in ServerPlayerCache.Instance.GetOnlinePlayers(name))
                {
                    if (player.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        target = player;
                        return true;
                    }
                
                    activePlayers.Add(player);
                }
            
                if (activePlayers.Count == 1)
                {
                    target = activePlayers[0];
                    return true;
                }
                
                IPlayer match = null;
                bool multiple = false;
                foreach (IPlayer player in ServerPlayerCache.Instance.GetAllPlayers(name))
                {
                    if (!multiple && player.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (match == null)
                        {
                            match = player;
                        }
                        else
                        {
                            match = null;
                            multiple = true;
                        }
                    }
                
                    foundPlayers.Add(player);

                    if (player.IsLinked())
                    {
                        linkedPlayers.Add(player);
                    }

                    if (foundPlayers.Count > 1 && linkedPlayers.Count > 1)
                    {
                        break;
                    }
                }

                if (!multiple && match != null)
                {
                    target = match;
                    return true;
                }
                
                target = null;
                if (foundPlayers.Count == 1)
                {
                    target = foundPlayers[0];
                    return true;
                }
                
                if (linkedPlayers.Count == 1)
                {
                    target = linkedPlayers[0];
                    return true;
                }

                if (foundPlayers.Count > 1)
                {
                    Chat(from, LangKeys.MultiplePlayersFound, GetDefault().Add(PlaceholderDataKeys.PlayerName, name));
                    return false;
                }
                
                Chat(from, LangKeys.NoPlayersFound, GetDefault().Add(PlaceholderDataKeys.PlayerName, name));
                return false;
            }
            finally
            { 
                Pool.FreeList(foundPlayers);
                Pool.FreeList(linkedPlayers);
                Pool.FreeList(activePlayers);
            }
        }

        public void SendPlayerPrivateMessage(IPlayer player, string serverLang, TemplateKey templateKey, PlaceholderData data)
        {
            ServerPrivateMessage(player, serverLang, data);
            DiscordPrivateMessage(player, templateKey, data);
        }

        public void LogPrivateMessage(PlaceholderData data)
        {
            LogSettings settings = _pluginConfig.Log;
            if (!settings.LogToConsole && !settings.LogToFile && _logChannel == null)
            {
                return;
            }

            string log = Lang(LangKeys.LogFormat, null, data);
            if (_pluginConfig.Log.LogToConsole)
            {
                Puts(log);
            }

            if (_pluginConfig.Log.LogToFile)
            {
                LogToFile(string.Empty, log, this);
            }

            _logChannel?.CreateGlobalTemplateMessage(Client, TemplateKeys.Messages.Log, null, data);
        }
        
        public void SendEffectToPlayer(IPlayer player)
        {
#if RUST
            if (!_pluginConfig.EnableEffectNotification)
            {
                return;
            }
            
            if (!player.IsConnected)
            {
                return;
            }
            
            BasePlayer basePlayer = player.Object as BasePlayer;
            if (basePlayer == null)
            {
                return;
            }
            
            Effect effect = _effect;
            effect.entity = basePlayer.net.ID;
            
            EffectNetwork.Send(effect, basePlayer.net.connection);
#endif
        }
        
        public void ServerPrivateMessage(IPlayer player, string langKey, PlaceholderData data)
        {
            if (player.IsConnected && player.Object != null)
            {
                player.Message(Lang(langKey, player, data));
                SendEffectToPlayer(player);
            }
        }

        public void DiscordPrivateMessage(IPlayer player, TemplateKey templateKey, PlaceholderData data)
        {
            if (player.IsLinked())
            {
                player.SendDiscordTemplateMessage(Client, templateKey, null, data);
            }
        }

        public void RegisterServerLangCommand(string command, string langKey)
        {
            HashSet<string> registered = new();
            foreach (string langType in lang.GetLanguages(this))
            {
                Dictionary<string, string> langKeys = lang.GetMessages(langType, this);
                if (langKeys.TryGetValue(langKey, out string commandValue) && !string.IsNullOrEmpty(commandValue) && registered.Add(commandValue))
                {
                    AddCovalenceCommand(commandValue, command);
                }
            }
        }

        public void Chat(IPlayer player, string key, PlaceholderData data)
        {
            if (player.IsConnected)
            {
                player.Reply(Lang(LangKeys.Chat, player, GetDefault().Add(PlaceholderDataKeys.Chat, Lang(key, player, data))));
            }
        }
        
        public string Lang(string key, IPlayer player = null) => lang.GetMessage(key, this, player?.Id);

        public string Lang(string key, IPlayer player, PlaceholderData data)
        {
            string result = _placeholders.ProcessPlaceholders(Lang(key, player), data);
            return result;
        }
        #endregion

        #region Classes
        private class PluginConfig
        {
            [DefaultValue("")]
            [JsonProperty(PropertyName = "Discord Bot Token")]
            public string DiscordApiKey { get; set; }

            [DefaultValue(true)]
            [JsonProperty(PropertyName = "Allow Discord Commands In Direct Messages")]
            public bool AllowInDm { get; set; }

#if RUST
            [DefaultValue(true)]
            [JsonProperty(PropertyName = "Enable Effect Notification")]
            public bool EnableEffectNotification { get; set; }
            
            [DefaultValue("assets/prefabs/tools/pager/effects/vibrate.prefab")]
            [JsonProperty(PropertyName = "Notification Effect")]
            public string EffectNotification { get; set; }
#endif

            [JsonProperty(PropertyName = "Log Settings")]
            public LogSettings Log { get; set; }
            
            [JsonConverter(typeof(StringEnumConverter))]
            [DefaultValue(DiscordLogLevel.Info)]
            [JsonProperty(PropertyName = "Discord Extension Log Level (Verbose, Debug, Info, Warning, Error, Exception, Off)")]
            public DiscordLogLevel ExtensionDebugging { get; set; }
        }
        
        public class LogSettings
        {
            [JsonProperty(PropertyName = "Log To Console")]
            public bool LogToConsole { get; set; }

            [JsonProperty(PropertyName = "Log To File")]
            public bool LogToFile { get; set; }
            
            [JsonProperty(PropertyName = "Log To Channel ID")]
            public Snowflake LogToChannelId { get; set; }

            public LogSettings(LogSettings settings)
            {
                LogToConsole = settings?.LogToConsole ?? true;
                LogToFile = settings?.LogToFile ?? false;
                LogToChannelId = settings?.LogToChannelId ?? default(Snowflake);
            }
        }

        private static class LangKeys
        {
            public const string Base = "V3.";
            
            public const string Chat = Base + nameof(Chat);
            public const string FromFormat = Base + nameof(FromFormat);
            public const string ToFormat = Base + nameof(ToFormat);
            public const string InvalidPmSyntax = Base + nameof(InvalidPmSyntax);
            public const string InvalidReplySyntax = Base + nameof(InvalidReplySyntax);
            public const string NoPreviousPm = Base + nameof(NoPreviousPm);
            public const string MultiplePlayersFound =  Base + nameof(MultiplePlayersFound);
            public const string NoPlayersFound = Base + nameof(NoPlayersFound);
            public const string LogFormat = Base + nameof(LogFormat);

            public const string ChatPmCommand = Base + "Commands.Chat.PM";
            public const string ChatReplyCommand = Base + "Commands.Chat.Reply";
        }

        private static class TemplateKeys
        {
            public static class Messages
            {
                private const string Base = nameof(Messages) + ".";
                
                public static readonly TemplateKey To = new(Base + nameof(To));
                public static readonly TemplateKey From = new(Base + nameof(From));
                public static readonly TemplateKey Log = new(Base + nameof(Log));
            }
            
            public static class Errors
            {
                private const string Base = nameof(Errors) + ".";

                public static readonly TemplateKey UnlinkedUser = new(Base + nameof(UnlinkedUser));
                public static readonly TemplateKey InvalidAutoCompleteSelection = new(Base + nameof(InvalidAutoCompleteSelection));
                public static readonly TemplateKey NoPreviousPm = new(Base + nameof(NoPreviousPm));
            }
        }

        private static class PlaceholderKeys
        {
            public static readonly PlaceholderKey Message = new(nameof(DiscordPM), "message");
            public static readonly PlaceholderKey Chat = new(nameof(DiscordPM), "chat");
            public static readonly PlaceholderKey NotFound = new(nameof(DiscordPM), "player.notfound");
        }

        private class PlaceholderDataKeys
        {
            public static readonly PlaceholderDataKey Message = new("pm.message");
            public static readonly PlaceholderDataKey PlayerName = new("pm.name");
            public static readonly PlaceholderDataKey Chat = new("pm.chat");
        }
        #endregion
    }
}