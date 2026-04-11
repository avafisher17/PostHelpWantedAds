using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace PostHelpWantedAds
{
    
    public class EscapableNamingMenu : NamingMenu
    {
        private Action _onCancel;
        private ClickableTextureComponent _cancelButton;

        public EscapableNamingMenu(doneNamingBehavior callback, string title, Action onCancel)
            : base(callback, title)
        {
            textBox.limitWidth = false;
            _onCancel = onCancel;
            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 64, yPositionOnScreen - 8, 48, 48),
                Game1.mouseCursors,
                new Rectangle(337, 494, 12, 12),
                4f
            );

            randomButton.bounds = new Rectangle(0, 0, 0, 0);
            randomButton.visible = false;
            randomButton.myID = -1;
            textBox.Text = "";
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.exitActiveMenu();
                _onCancel?.Invoke();
            }
            else
            {
                base.receiveKeyPress(key);
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_cancelButton.containsPoint(x, y))
            {
                Game1.exitActiveMenu();
                _onCancel?.Invoke();
                return;
            }
            base.receiveLeftClick(x, y, playSound);
        }

        public override void draw(SpriteBatch b)
        {
            base.draw(b);
            _cancelButton.draw(b);
        }
    }
}
