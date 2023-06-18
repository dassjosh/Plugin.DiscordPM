using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Ext.Discord.Attributes.ApplicationCommands;
using Oxide.Ext.Discord.Attributes.Pooling;
using Oxide.Ext.Discord.Builders.ApplicationCommands;
using Oxide.Ext.Discord.Builders.Interactions;
using Oxide.Ext.Discord.Builders.Interactions.AutoComplete;
using Oxide.Ext.Discord.Cache;
using Oxide.Ext.Discord.Clients;
using Oxide.Ext.Discord.Connections;
using Oxide.Ext.Discord.Constants;
using Oxide.Ext.Discord.Entities;
using Oxide.Ext.Discord.Entities.Api;
using Oxide.Ext.Discord.Entities.Channels;
using Oxide.Ext.Discord.Entities.Gateway;
using Oxide.Ext.Discord.Entities.Guilds;
using Oxide.Ext.Discord.Entities.Interactions;
using Oxide.Ext.Discord.Entities.Interactions.ApplicationCommands;
using Oxide.Ext.Discord.Entities.Interactions.Response;
using Oxide.Ext.Discord.Entities.Messages;
using Oxide.Ext.Discord.Entities.Permissions;
using Oxide.Ext.Discord.Extensions;
using Oxide.Ext.Discord.Interfaces;
using Oxide.Ext.Discord.Libraries.Placeholders;
using Oxide.Ext.Discord.Libraries.Placeholders.Default;
using Oxide.Ext.Discord.Libraries.Templates;
using Oxide.Ext.Discord.Libraries.Templates.Commands;
using Oxide.Ext.Discord.Libraries.Templates.Embeds;
using Oxide.Ext.Discord.Libraries.Templates.Messages;
using Oxide.Ext.Discord.Logging;
using Oxide.Ext.Discord.Pooling;

#if RUST
using UnityEngine;
#endif

namespace Oxide.Plugins
{
    // ReSharper disable once UnusedType.Global
    [Info("Discord PM", "MJSU", "2.0.4")]
    [Description("Allows private messaging through discord")]
    internal class DiscordPM : CovalencePlugin, IDiscordPlugin
    {
        #region Class Fields
        [PluginReference]
        private Plugin Clans;

        public DiscordClient Client { get; set; }
        
        private PluginConfig _pluginConfig;
        private PluginData _pluginData;

        private const string AccentColor = "de8732";
        private const string PmCommand = "pm";
        private const string ReplyCommand = "r";
        private const string NameArg = "name";
        private const string MessageArg = "message";

        [DiscordPool]
        private DiscordPluginPool _pool;
        private readonly DiscordPlaceholders _placeholders = GetLibrary<DiscordPlaceholders>();
        private readonly DiscordMessageTemplates _templates = GetLibrary<DiscordMessageTemplates>();
        private readonly DiscordCommandLocalizations _localizations = GetLibrary<DiscordCommandLocalizations>();
        private readonly PlayerNameFormatter _nameFormatter = PlayerNameFormatter.Create(PlayerDisplayNameMode.IncludeClanName);
        
        private readonly Hash<string, IPlayer> _replies = new Hash<string, IPlayer>();
        private readonly Hash<string, string> _nameCache = new Hash<string, string>();
        
        private readonly BotConnection  _discordSettings = new BotConnection
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

            _pluginData = Interface.Oxide.DataFileSystem.ReadObject<PluginData>(Name);
        }
        
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [LangKeys.Chat] = $"[#BEBEBE][[#{AccentColor}]{Title}[/#]] {{0}}[/#]",
                [LangKeys.ToFormat] = $"[#BEBEBE][#{AccentColor}]PM to {PlaceholderKeys.To}:[/#] {PlaceholderKeys.Message}[/#]",
                [LangKeys.FromFormat] = $"[#BEBEBE][#{AccentColor}]PM from {PlaceholderKeys.From}:[/#] {PlaceholderKeys.Message}[/#]",
                [LangKeys.LogFormat] = $"{PlaceholderKeys.From} -> {PlaceholderKeys.To}: {PlaceholderKeys.Message}",
                
                [LangKeys.InvalidPmSyntax] = $"Invalid Syntax. Type [#{AccentColor}]/{{plugin.lang:{LangKeys.ChatPmCommand}}} MJSU Hi![/#]",
                [LangKeys.InvalidReplySyntax] = $"Invalid Syntax. Ex: [#{AccentColor}]/{{plugin.lang:{LangKeys.ChatReplyCommand}}} Hi![/#]",
                [LangKeys.NoPreviousPm] = $"You do not have any previous discord PM's. Please use /{{plugin.lang:{LangKeys.ChatPmCommand}}} to be able to use this command.",
                [LangKeys.NoPlayersFound] = "No players found with the name '{0}'",
                [LangKeys.MultiplePlayersFound] = "Multiple players found with the name '{0}'.",

                [LangKeys.ChatPmCommand] = "pm",
                [LangKeys.ChatReplyCommand] = "r",
            }, this);
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
            
            Client.Connect(_discordSettings);

            RegisterPlaceholders();
            RegisterTemplates();
            
#if RUST
            if (_pluginConfig.EnableEffectNotification)
            {
                _effect = new Effect(_pluginConfig.EffectNotification, Vector3.zero, Vector3.zero);
                _effect.attached = true;
            }
#endif
        }

        // ReSharper disable once UnusedMember.Local
        private void OnUserConnected(IPlayer player)
        {
            _nameCache.Remove(player.Id);
        }
        #endregion

        #region Chat Commands
        // ReSharper disable once UnusedParameter.Local
        private void DiscordPmChatCommand(IPlayer player, string cmd, string[] args)
        {
            if (args.Length < 2)
            {
                Chat(player, Lang(LangKeys.InvalidPmSyntax, player, GetDefault()));
                return;
            }

            object search = FindPlayer(args[0]);
            if (search is string)
            {
                Chat(player, Lang((string) search, player, args[0]));
                return;
            }

            IPlayer searchedPlayer = search as IPlayer;
            if (searchedPlayer == null)
            {
                Chat(player, Lang(LangKeys.NoPlayersFound, player, args[0]));
                return;
            }

            _replies[player.Id] = searchedPlayer;
            _replies[searchedPlayer.Id] = player;

            string message = args.Length == 2 ? args[1] : string.Join(" ", args.Skip(1).ToArray());
            
            SendPrivateMessageFromServer(player, searchedPlayer, message);
        }

        // ReSharper disable once UnusedParameter.Local
        private void DiscordPmChatReplyCommand(IPlayer player, string cmd, string[] args)
        {
            if (args.Length < 1)
            {
                Chat(player, Lang(LangKeys.InvalidReplySyntax, player, GetDefault()));
                return;
            }

            IPlayer target = _replies[player.Id];
            if (target == null)
            {
                Chat(player, Lang(LangKeys.NoPreviousPm, player));
                return;
            }
            
            string message = args.Length == 1 ? args[0] : string.Join(" ", args);
            SendPrivateMessageFromServer(player, target, message);
        }
        
        public void SendPrivateMessageFromServer(IPlayer sender, IPlayer target, string message)
        {
            using (PlaceholderData placeholders = GetPmDefault(sender, target, message))
            {
                placeholders.ManualPool();
                SendPlayerPrivateMessage(sender, target, message, LangKeys.ToFormat, TemplateKeys.Messages.To);
                SendPlayerPrivateMessage(target, sender, message, LangKeys.FromFormat, TemplateKeys.Messages.From);
                LogPrivateMessage(sender, target, message);
            }
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

            _localizations.RegisterCommandLocalizationAsync(this, "PM.Command", localization, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0, 0)).Then(() =>
            {
                _localizations.ApplyCommandLocalizationsAsync(this, cmd, "PM.Command").Then(() =>
                {
                    Client.Bot.Application.CreateGlobalCommand(Client, pmCommand.Build()).Then(command =>
                    {
                        Snowflake oldId = _pluginData.PmCommandId;
                        if (oldId == command.Id)
                        {
                            return;
                        }

                        if (oldId.IsValid() && oldId != command.Id)
                        {
                            Client.Bot.Application.GetGlobalCommand(Client, oldId)
                                  .Then(oldCommand => oldCommand.Delete(Client))
                                  .Catch<ResponseError>(error => error.SuppressErrorMessage());
                        }

                        _pluginData.PmCommandId = command.Id;
                        _pmCommand = command;
                        SaveData();
                    });
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

            _localizations.RegisterCommandLocalizationAsync(this, "Reply.Command", localization, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0, 0)).Then(() =>
            {
                _localizations.ApplyCommandLocalizationsAsync(this, cmd, "Reply.Command").Then(() =>
                {
                    Client.Bot.Application.CreateGlobalCommand(Client, replyCommand.Build()).Then(command =>
                    {
                        Snowflake oldId = _pluginData.ReplyCommandId;
                        if (oldId == command.Id)
                        {
                            return;
                        }

                        if (oldId.IsValid() && oldId != command.Id)
                        {
                            Client.Bot.Application.GetGlobalCommand(Client, oldId).Then(oldCommand => oldCommand.Delete(Client)).Catch<ResponseError>(error => error.SuppressErrorMessage());
                        }

                        _pluginData.ReplyCommandId = command.Id;
                        SaveData();
                    });
                });
            });
        }

        public void AddCommandNameOption(ApplicationCommandBuilder builder)
        {
            builder.AddOption(CommandOptionType.String, NameArg, "Name of the player")
                   .Required()
                   .AutoComplete()
                   .Build();
        }
        
        public void AddCommandMessageOption(ApplicationCommandBuilder builder)
        {
            builder.AddOption(CommandOptionType.String, MessageArg, "Message to send the player")
                   .Required()
                   .Build();
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
                interaction.CreateTemplateResponse(Client, this, InteractionResponseType.ChannelMessageWithSource, TemplateKeys.Errors.UnlinkedUser, GetInteractionCallback(interaction), GetDefault());
                return;
            }

            string targetId = parsed.Args.GetString(NameArg);
            string message = parsed.Args.GetString(MessageArg);

            IPlayer target = players.FindPlayerById(targetId);
            if (target == null)
            {
                interaction.CreateTemplateResponse(Client, this, InteractionResponseType.ChannelMessageWithSource, TemplateKeys.Errors.InvalidAutoCompleteSelection, GetInteractionCallback(interaction), GetDefault());
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
                interaction.CreateTemplateResponse(Client, this, InteractionResponseType.ChannelMessageWithSource, TemplateKeys.Errors.UnlinkedUser, GetInteractionCallback(interaction), GetDefault());
                return;
            }
            
            string message = parsed.Args.GetString(MessageArg);

            IPlayer target = _replies[player.Id];
            if (target == null)
            {
                interaction.CreateTemplateResponse(Client, this, InteractionResponseType.ChannelMessageWithSource, TemplateKeys.Errors.NoPreviousPm, GetInteractionCallback(interaction), GetDefault().AddCommand(_pmCommand));
                return;
            }
            
            _replies[target.Id] = player;
            
            SendPrivateMessageFromDiscord(interaction, player, target, message);
        }
        
        public void SendPrivateMessageFromDiscord(DiscordInteraction interaction, IPlayer player, IPlayer target, string message)
        {
            if (!interaction.GuildId.HasValue)
            {
                ServerPrivateMessage(player, target, message, LangKeys.ToFormat);
            }
            else
            {
                SendPlayerPrivateMessage(player, target, message, LangKeys.ToFormat, TemplateKeys.Messages.To);
            }
            interaction.CreateTemplateResponse(Client, this, InteractionResponseType.ChannelMessageWithSource, TemplateKeys.Messages.To, GetInteractionCallback(interaction), GetPmDefault(player, target, message));
            SendPlayerPrivateMessage(target, player, message, LangKeys.FromFormat, TemplateKeys.Messages.From);
            LogPrivateMessage(player, target, message);
        }

        // ReSharper disable once UnusedMember.Local
        [DiscordAutoCompleteCommand(PmCommand, NameArg)]
        private void HandleNameAutoComplete(DiscordInteraction interaction, InteractionDataOption focused)
        {
            string search = focused.GetValue<string>();
            InteractionAutoCompleteBuilder response = interaction.GetAutoCompleteBuilder();
            response.AddOnlinePlayers(search, _nameFormatter);
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
            PlayerPlaceholders.RegisterPlaceholders(this, "discordpm.from", PlaceholderKeys.Data.From);
            PlayerPlaceholders.RegisterPlaceholders(this, "discordpm.to", PlaceholderKeys.Data.To);
            _placeholders.RegisterPlaceholder<IPlayer>(this, PlaceholderKeys.From, PlaceholderKeys.Data.From, PlayerName);
            _placeholders.RegisterPlaceholder<IPlayer>(this, PlaceholderKeys.To, PlaceholderKeys.Data.To, PlayerName);
            _placeholders.RegisterPlaceholder<string>(this, PlaceholderKeys.Message,  PlaceholderKeys.Data.Message, PlaceholderFormatting.Replace);
        }
        
        public void PlayerName(StringBuilder builder, PlaceholderState state, IPlayer player) => PlaceholderFormatting.Replace(builder, state, GetPlayerName(player));

        public PlaceholderData GetPmDefault(IPlayer from, IPlayer to, string message)
        {
            return GetDefault()
                   .Add(PlaceholderKeys.Data.From, from)
                   .Add(PlaceholderKeys.Data.To, to)
                   .Add(PlaceholderKeys.Data.Message, message);
        }
        
        public PlaceholderData GetDefault()
        {
            return _placeholders.CreateData(this).AddNowTimestamp();
        }
        #endregion

        #region Discord Templates
        public void RegisterTemplates()
        {
            DiscordMessageTemplate toMessage = CreateTemplateEmbed($"[{{timestamp.shortime}}] PM to {PlaceholderKeys.To}: {PlaceholderKeys.Message}", DiscordColor.Success.ToHex());
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Messages.To, toMessage, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
            
            DiscordMessageTemplate fromMessage = CreateTemplateEmbed($"[{{timestamp.shortime}}] PM from {PlaceholderKeys.From}: {PlaceholderKeys.Message}", DiscordColor.Danger.ToHex());
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Messages.From, fromMessage, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));       
            
            DiscordMessageTemplate logMessage = CreateTemplateEmbed($"{PlaceholderKeys.From} -> {PlaceholderKeys.To}: {PlaceholderKeys.Message}", DiscordColor.Danger.ToHex());
            _templates.RegisterGlobalTemplateAsync(this, TemplateKeys.Messages.Log, logMessage, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
            
            DiscordMessageTemplate errorUnlinkedUser = CreatePrefixedTemplateEmbed("You cannot use this command until you're have linked your game and discord accounts", DiscordColor.Danger.ToHex());
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Errors.UnlinkedUser, errorUnlinkedUser, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
            
            DiscordMessageTemplate errorInvalidAutoComplete = CreatePrefixedTemplateEmbed("The name you have picked does not appear to be a valid auto complete value. Please make sure you select one of the auto complete options.", DiscordColor.Danger.ToHex());
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Errors.InvalidAutoCompleteSelection, errorInvalidAutoComplete, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
            
            DiscordMessageTemplate errorNoPreviousPm = CreatePrefixedTemplateEmbed("You do not have any previous discord PM's. Please use {command.mention} to be able to use this command.", DiscordColor.Danger.ToHex());
            _templates.RegisterLocalizedTemplateAsync(this, TemplateKeys.Errors.NoPreviousPm, errorNoPreviousPm, new TemplateVersion(1, 0, 0), new TemplateVersion(1, 0 ,0));
        }
        
        public DiscordMessageTemplate CreateTemplateEmbed(string description, string color)
        {
            return new DiscordMessageTemplate
            {
                Embeds = new List<DiscordEmbedTemplate>
                {
                    new DiscordEmbedTemplate
                    {
                        Description = description,
                        Color = $"#{color}"
                    }
                }
            };
        }
        
        public DiscordMessageTemplate CreatePrefixedTemplateEmbed(string description, string color)
        {
            return new DiscordMessageTemplate
            {
                Embeds = new List<DiscordEmbedTemplate>
                {
                    new DiscordEmbedTemplate
                    {
                        Description = $"[{{plugin.title}}] {description}",
                        Color = $"#{color}"
                    }
                }
            };
        }
        #endregion
        
        #region Helpers
        public object FindPlayer(string name)
        {
            List<IPlayer> foundPlayers = _pool.GetList<IPlayer>();
            List<IPlayer> activePlayers = _pool.GetList<IPlayer>();

            foreach (IPlayer player in ServerPlayerCache.Instance.GetAllPlayers(name))
            {
                foundPlayers.Add(player);
                if (player.IsLinked() || player.IsConnected)
                {
                    activePlayers.Add(player);
                }
            }

            object result;
            if (foundPlayers.Count == 1)
            {
                result = foundPlayers[0];
            } 
            else if (activePlayers.Count == 1)
            {
                result = activePlayers[0];
            }
            else if (foundPlayers.Count > 1)
            {
                result = LangKeys.MultiplePlayersFound;
            }
            else
            {
                result = LangKeys.NoPlayersFound;
            }
            
            _pool.FreeList(foundPlayers);
            _pool.FreeList(activePlayers);
            return result;
        }

        public void Chat(IPlayer player, string key, params object[] args)
        {
            if (player.IsConnected)
            {
                player.Reply(Lang(LangKeys.Chat, player, Lang(key, player, args)));
            }
        }

        public void SendPlayerPrivateMessage(IPlayer player, IPlayer target, string message, string serverLang, string templateKey)
        {
            ServerPrivateMessage(player, target, message, serverLang);
            DiscordPrivateMessage(player, target, message, templateKey);
        }

        public void LogPrivateMessage(IPlayer player, IPlayer target, string message)
        {
            LogSettings settings = _pluginConfig.Log;
            if (!settings.LogToConsole && !settings.LogToFile && _logChannel == null)
            {
                return;
            }

            string log = Lang(LangKeys.LogFormat, null, player.Name, target.Name, message);
            if (_pluginConfig.Log.LogToConsole)
            {
                Puts(log);
            }

            if (_pluginConfig.Log.LogToFile)
            {
                LogToFile(string.Empty, log, this);
            }

            _logChannel?.CreateGlobalTemplateMessage(Client, this, TemplateKeys.Messages.Log, null, GetPmDefault(player, target, message));
        }

        public string GetPlayerName(IPlayer player)
        {
            string name = _nameCache[player.Id];
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            string clanTag = Clans != null && Clans.IsLoaded ? Clans.Call<string>("GetClanOf", player.Id) : null;
            name = !string.IsNullOrEmpty(clanTag) ? $"[{clanTag}] {player.Name}" : player.Name;
            
            _nameCache[player.Id] = name;
            return name;
        }

#if RUST
        public void SendEffectToPlayer(IPlayer player)
        {
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
        }
#endif

        public void ServerPrivateMessage(IPlayer player, IPlayer target, string message, string langKey)
        {
            if (player.IsConnected)
            {
                player.Message(Lang(langKey, player, GetPmDefault(player, target, message)));
#if RUST
                SendEffectToPlayer(player);
#endif
            }
        }

        public void DiscordPrivateMessage(IPlayer player, IPlayer target, string message, string templateKey)
        {
            if (player.IsLinked())
            {
                player.SendDiscordTemplateMessage(Client, this, templateKey, null, GetPmDefault(player, target, message));
            }
        }

        public void RegisterServerLangCommand(string command, string langKey)
        {
            foreach (string langType in lang.GetLanguages(this))
            {
                Dictionary<string, string> langKeys = lang.GetMessages(langType, this);
                string commandValue;
                if (langKeys.TryGetValue(langKey, out commandValue) && !string.IsNullOrEmpty(commandValue))
                {
                    AddCovalenceCommand(commandValue, command);
                }
            }
        }

        public string Lang(string key, IPlayer player = null) => lang.GetMessage(key, this, player?.Id);

        public string Lang(string key, IPlayer player = null, params object[] args)
        {
            string lang = Lang(key, player);
            try
            {
                return string.Format(lang, args);
            }
            catch (Exception ex)
            {
                PrintError($"Lang Key '{key}'\nMessage:{lang}\nException:{ex}");
                throw;
            }
        }

        public string Lang(string key, IPlayer player, PlaceholderData data) => _placeholders.ProcessPlaceholders(Lang(key, player), data);

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _pluginData);
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
        
        public class PluginData
        {
            public Snowflake PmCommandId { get; set; }
            public Snowflake ReplyCommandId { get; set; }
        }

        private static class LangKeys
        {
            public const string Chat = nameof(Chat);
            public const string FromFormat = nameof(FromFormat);
            public const string ToFormat = nameof(ToFormat);
            public const string InvalidPmSyntax = nameof(InvalidPmSyntax) + "V1";
            public const string InvalidReplySyntax = nameof(InvalidReplySyntax) + "V1";
            public const string NoPreviousPm = nameof(NoPreviousPm);
            public const string MultiplePlayersFound =  nameof(MultiplePlayersFound);
            public const string NoPlayersFound = nameof(NoPlayersFound);
            public const string LogFormat = nameof(LogFormat);

            public const string ChatPmCommand = "Commands.Chat.PM";
            public const string ChatReplyCommand = "Commands.Chat.Reply";
        }

        private static class TemplateKeys
        {
            public static class Messages
            {
                private const string Base = nameof(Messages) + ".";
                
                public const string To = Base + nameof(To);
                public const string From = Base + nameof(From);
                public const string Log = Base + nameof(Log);
            }
            
            public static class Errors
            {
                private const string Base = nameof(Errors) + ".";

                public const string UnlinkedUser = Base + nameof(UnlinkedUser);
                public const string InvalidAutoCompleteSelection = Base + nameof(InvalidAutoCompleteSelection);
                public const string NoPreviousPm = Base + nameof(NoPreviousPm);
            }
        }

        private static class PlaceholderKeys
        {
            public const string From = "discordpm.from.player.name";
            public const string To = "discordpm.to.player.name";
            public const string Message = "discordpm.message";

            public static class Data
            {
                public const string From = "from.player";
                public const string To = "to.player";
                public const string Message = "discordpm.message";
            }
        }
        #endregion
    }
}