using System.Threading.Tasks;
using Discord.Commands;

namespace nhitomi.Modules
{
    public class UserModule : ModuleBase
    {
        readonly IDatabase _database;
        readonly MessageFormatter _formatter;

        public UserModule(IDatabase database, MessageFormatter formatter)
        {
            _database = database;
            _formatter = formatter;
        }

        [Command("subscribe")]
        [Alias("sub")]
        [Summary("Adds a subscription to the specified tag.")]
        public async Task SubscribeAsync([Remainder] string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return;

            using (Context.Channel.EnterTypingState())
            {
                await _database.AddTagSubscriptionAsync(Context.User.Id, tag);

                await ReplyAsync(_formatter.SubscribeSuccess(tag));
            }
        }

        [Command("unsubscribe")]
        [Alias("unsub")]
        [Summary("Removes subscription from the specified tag.")]
        public async Task UnsubscribeAsync([Remainder] string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return;

            using (Context.Channel.EnterTypingState())
            {
                await _database.RemoveTagSubscriptionAsync(Context.User.Id, tag);

                await ReplyAsync(_formatter.UnsubscribeSuccess(tag));
            }
        }

        [Command("subscriptions")]
        [Alias("subs")]
        [Summary("Lists all tags you are subscribed to.")]
        public async Task ListSubscriptionsAsync()
        {
            using (Context.Channel.EnterTypingState())
            {
                var tags = await _database.GetTagSubscriptionsAsync(Context.User.Id);

                await ReplyAsync(embed: _formatter.CreateSubscriptionListEmbed(tags));
            }
        }
    }
}