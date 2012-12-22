using System;
using MonoTouch.UIKit;
using Xamarin.Data;
using System.IO;

namespace InstantSample
{
	public class ContactViewController : UITableViewController
	{
		MySource<Person> source;
		public ContactViewController ()
		{
			source = new MySource<Person> ();
			this.TableView.Source = source;
		}

		public override void ViewDidLoad ()
		{
			base.ViewDidLoad ();
			//source.db.Precache<Song> ();

		}
		public override void DidReceiveMemoryWarning ()
		{
			base.DidReceiveMemoryWarning ();
			Database.Main.ClearMemory ();
		}
		class MySource<T> : UITableViewSource  where T : new()
		{
			public MySource () {
				Database.Main.MakeClassInstant<T>();
			}
			#region implemented abstract members of UITableViewSource
			public override int NumberOfSections (UITableView tableView)
			{
				return Database.Main.NumberOfSections<T> ();
			}

			public override int RowsInSection (UITableView tableview, int section)
			{
				return Database.Main.RowsInSection<T> (section);
			}

			public override UITableViewCell GetCell (UITableView tableView, MonoTouch.Foundation.NSIndexPath indexPath)
			{
				var cell = tableView.DequeueReusableCell ("person");
				if (cell == null)
					cell = new UITableViewCell (UITableViewCellStyle.Subtitle, "person");
				var obj = ((object)Database.Main.ObjectForRow<T> (indexPath.Section, indexPath.Row));
				var person = (Person)obj;
				cell.TextLabel.Text = person.ToString();
				cell.DetailTextLabel.Text = person.Email;
				return cell;
			}
			public override string TitleForHeader (UITableView tableView, int section)
			{
				return Database.Main.SectionHeader<T> (section);
			}
			public override string[] SectionIndexTitles (UITableView tableView)
			{
				return Database.Main.QuickJump<T> ();
			}

			#endregion


		}
	}
}

