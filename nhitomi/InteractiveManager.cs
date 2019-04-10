// Copyright (c) 2018-2019 chiya.dev
//
// This software is released under the MIT License.
// https://opensource.org/licenses/MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using nhitomi.Core;
using nhitomi.Database;
using nhitomi.Services;

namespace nhitomi
{
    public delegate Task<IUserMessage> SendMessageAsync(
        string text = null,
        bool isTTS = false,
        Embed embed = null,
        RequestOptions options = null);

    public abstract class ListInteractive
    {
        public IUserMessage Message;

        public readonly IEnumerableBrowser Browser;

        protected ListInteractive(IEnumerableBrowser browser)
        {
            Browser = browser;
        }

        public abstract Embed CreateEmbed(MessageFormatter formatter);

        public virtual Task UpdateContents(MessageFormatter formatter) =>
            Message.ModifyAsync(null, CreateEmbed(formatter));

        public void Dispose() => Browser.Dispose();
    }

    public class DoujinListInteractive : ListInteractive
    {
        public IUserMessage DownloadMessage;

        public DoujinListInteractive(IAsyncEnumerable<IDoujin> doujins)
            : base(new EnumerableBrowser<IDoujin>(doujins.GetEnumerator()))
        {
        }

        IDoujin Current => ((IAsyncEnumerator<IDoujin>) Browser).Current;

        public override Embed CreateEmbed(MessageFormatter formatter) => formatter.CreateDoujinEmbed(Current);

        public override async Task UpdateContents(MessageFormatter formatter)
        {
            await base.UpdateContents(formatter);

            if (DownloadMessage != null)
                await DownloadMessage.ModifyAsync(embed: formatter.CreateDownloadEmbed(Current));
        }
    }

    public class CollectionInteractive : ListInteractive
    {
        const int _itemsPerPage = 14;

        readonly string _collectionName;

        public CollectionInteractive(string collectionName, IEnumerable<CollectionItemInfo> items) : base(
            new EnumerableBrowser<IEnumerable<CollectionItemInfo>>(
                items.ChunkBy(_itemsPerPage).ToAsyncEnumerable().GetEnumerator()))
        {
            _collectionName = collectionName;
        }

        IEnumerable<CollectionItemInfo> Current =>
            ((IAsyncEnumerator<IEnumerable<CollectionItemInfo>>) Browser).Current;

        public override Embed CreateEmbed(MessageFormatter formatter) =>
            formatter.CreateCollectionEmbed(_collectionName, Current.ToArray());
    }

    public class InteractiveManager : IDisposable
    {
        readonly AppSettings _settings;
        readonly DiscordService _discord;
        readonly MessageFormatter _formatter;
        readonly ISet<IDoujinClient> _clients;
        readonly ILogger<InteractiveManager> _logger;

        public InteractiveManager(
            IOptions<AppSettings> options,
            DiscordService discord,
            MessageFormatter formatter,
            ISet<IDoujinClient> clients,
            ILogger<InteractiveManager> logger)
        {
            _settings = options.Value;
            _discord = discord;
            _formatter = formatter;
            _logger = logger;
            _discord = discord;
            _clients = clients;

            _discord.Socket.ReactionAdded += HandleReactionAddedAsyncBackground;
            _discord.Socket.ReactionRemoved += HandleReactionRemovedAsyncBackground;

            _discord.DoujinsDetected += HandleDoujinsDetected;
        }

        readonly ConcurrentDictionary<ulong, ListInteractive>
            _listInteractives = new ConcurrentDictionary<ulong, ListInteractive>();

        async Task<bool> CreateListInteractiveAsync(
            ListInteractive interactive,
            SendMessageAsync sendMessage,
            CancellationToken cancellationToken = default)
        {
            // ensure list is not empty
            if (!await interactive.Browser.MoveNext(cancellationToken))
            {
                interactive.Dispose();
                await sendMessage(_formatter.EmptyList());

                return false;
            }

            // send interactive message
            interactive.Message = await sendMessage(embed: interactive.CreateEmbed(_formatter));

            // if list contains only one item, don't proceed to create the list
            if (!await interactive.Browser.MoveNext(cancellationToken))
            {
                interactive.Dispose();
                return true;
            }

            interactive.Browser.MovePrevious();

            // register interactive
            _listInteractives.AddOrUpdate(interactive.Message.Id, interactive, (a, b) => interactive);

            // schedule expiry
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(_settings.Discord.Command.InteractiveExpiry), default);

                if (_listInteractives.TryRemove(interactive.Message.Id, out var i))
                    await i.Message.DeleteAsync();
            }, default);

            // add paging triggers
            await _formatter.AddListTriggersAsync(interactive.Message);

            return true;
        }

        public async Task<DoujinListInteractive> CreateDoujinListInteractiveAsync(
            IAsyncEnumerable<IDoujin> doujins,
            SendMessageAsync sendMessage,
            CancellationToken cancellationToken = default)
        {
            var interactive = new DoujinListInteractive(doujins);

            return await CreateListInteractiveAsync(interactive, sendMessage, cancellationToken) ? interactive : null;
        }

        public async Task<CollectionInteractive> CreateCollectionInteractiveAsync(
            string collectionName,
            IEnumerable<CollectionItemInfo> items,
            SendMessageAsync sendMessage,
            CancellationToken cancellationToken = default)
        {
            var interactive = new CollectionInteractive(collectionName, items);

            return await CreateListInteractiveAsync(interactive, sendMessage, cancellationToken) ? interactive : null;
        }

        async Task HandleDoujinsDetected(IUserMessage message, IAsyncEnumerable<IDoujin> doujins)
        {
            var interactive = await CreateDoujinListInteractiveAsync(doujins, message.Channel.SendMessageAsync);

            if (interactive != null)
                await _formatter.AddDoujinTriggersAsync(interactive.Message);
        }

        Task HandleReactionAddedAsyncBackground(
            Cacheable<IUserMessage, ulong> cacheable,
            ISocketMessageChannel channel,
            SocketReaction reaction)
        {
            Task.Run(() => HandleReaction(reaction));
            return Task.CompletedTask;
        }

        Task HandleReactionRemovedAsyncBackground(
            Cacheable<IUserMessage, ulong> cacheable,
            ISocketMessageChannel channel,
            SocketReaction reaction)
        {
            Task.Run(() => HandleReaction(reaction));
            return Task.CompletedTask;
        }

        async Task HandleReaction(SocketReaction reaction)
        {
            try
            {
                // don't trigger reactions ourselves
                if (reaction.UserId == _discord.Socket.CurrentUser.Id)
                    return;

                // get interactive message
                if (!(await reaction.Channel.GetMessageAsync(reaction.MessageId) is IUserMessage message))
                    return;

                // interactive must be authored by us
                if (message.Author.Id != _discord.Socket.CurrentUser.Id ||
                    !message.Reactions.TryGetValue(reaction.Emote, out var metadata) ||
                    !metadata.IsMe)
                    return;

                if (await HandleDeleteReaction(reaction, message) ||
                    await HandleListInteractiveReaction(reaction, message) ||
                    await HandleDoujinDownloadReaction(reaction, message) ||
                    await HandleDoujinFavoriteReaction(reaction, message))
                    return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e,
                    $"Exception while handling reaction {reaction.Emote.Name} by user {reaction.UserId}: {e.Message}");

                await reaction.Channel.SendMessageAsync(embed: _formatter.CreateErrorEmbed());
            }
        }

        async Task<bool> HandleListInteractiveReaction(IReaction reaction, IMessage message)
        {
            if (!_listInteractives.TryGetValue(message.Id, out var interactive))
                return false;

            // left arrow
            if (reaction.Emote.Equals(MessageFormatter.LeftArrowEmote))
            {
                if (interactive.Browser.MovePrevious())
                    await interactive.UpdateContents(_formatter);
                else
                    await interactive.Message.ModifyAsync(m => { m.Content = _formatter.BeginningOfList; });

                return true;
            }

            // right arrow
            if (reaction.Emote.Equals(MessageFormatter.RightArrowEmote))
            {
                if (await interactive.Browser.MoveNext())
                    await interactive.UpdateContents(_formatter);
                else
                    await interactive.Message.ModifyAsync(m => { m.Content = _formatter.EndOfList; });

                return false;
            }

            return false;
        }

        async Task<bool> HandleDeleteReaction(IReaction reaction, IMessage message)
        {
            try
            {
                if (!reaction.Emote.Equals(MessageFormatter.TrashcanEmote))
                    return false;

                // destroy interactive if it is one
                if (_listInteractives.TryRemove(message.Id, out var interactive))
                    interactive.Dispose();

                // delete message
                await message.DeleteAsync();

                return true;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"Could not delete message {message.Id}");
                return true;
            }
        }

        async Task<IDoujin> GetDoujinFromMessage(IMessage message)
        {
            // source/id
            var identifier = message.Embeds.FirstOrDefault(e => e is Embed)?.Footer?.Text;

            if (identifier == null)
                return null;

            identifier.Split('/', 2).Destructure(out var source, out var id);

            var client = _clients.FindByName(source);
            if (client == null)
                return null;

            return await client.GetAsync(id);
        }

        async Task<bool> HandleDoujinDownloadReaction(IReaction reaction, IMessage message)
        {
            if (!reaction.Emote.Equals(MessageFormatter.FloppyDiskEmote))
                return false;

            var doujin = await GetDoujinFromMessage(message);
            if (doujin == null)
                return false;

            var downloadMessage =
                await message.Channel.SendMessageAsync(embed: _formatter.CreateDownloadEmbed(doujin));

            if (_listInteractives.TryGetValue(message.Id, out var interactive) &&
                interactive is DoujinListInteractive doujinListInteractive)
                doujinListInteractive.DownloadMessage = downloadMessage;

            return true;
        }

        async Task<bool> HandleDoujinFavoriteReaction(SocketReaction reaction, IMessage message)
        {
            if (!reaction.Emote.Equals(MessageFormatter.HeartEmote))
                return false;

            var doujin = await GetDoujinFromMessage(message);
            if (doujin == null)
                return false;

            var doujinMessage = await (await _discord.Socket.GetUser(reaction.UserId).GetOrCreateDMChannelAsync())
                .SendMessageAsync(embed: _formatter.CreateDoujinEmbed(doujin));

            await _formatter.AddDoujinTriggersAsync(doujinMessage);

            return true;
        }

        public void Dispose()
        {
            _discord.Socket.ReactionAdded -= HandleReactionAddedAsyncBackground;
            _discord.Socket.ReactionRemoved -= HandleReactionRemovedAsyncBackground;

            _discord.DoujinsDetected -= HandleDoujinsDetected;
        }
    }
}