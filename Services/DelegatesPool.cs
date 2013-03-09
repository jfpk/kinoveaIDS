/*
Copyright � Joan Charmant 2008.
joan.charmant@gmail.com 
 
This file is part of Kinovea.

Kinovea is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License version 2 
as published by the Free Software Foundation.

Kinovea is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with Kinovea. If not, see http://www.gnu.org/licenses/.

*/

using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Kinovea.Services
{
    
    // 2013-03-06 - Before adding to this file, see if it makes more sense to use the Notification Center instead.
    // Ultimately the DelegatesPool will be phased out in favor of the Notification Center.
    // A notification center can also be set up in a lower level module. See Kinovea.Camera.CameraTypeManager for example.
    
    
    //----------------------------------------------------------------------------------------------------------
    // The delegates pool is an area to share services between distant modules.
    // When a module exposes functionnality that will be accessed from an lower level
    // or from a sibling module, it should be done through the delegates pool
    // (instead of dependency injection or delegates tunnels).
    // 
    // The variable is filled by the server module, and called by the consumer.
    //
    // We don't use the Action<T1, T2, ...> shortcuts for delegate types, as it makes the usage of the delegate 
    // obscure for the caller. Since the caller doesn't know about the implementer, 
    // the prototype of the delegate is the only place where he can guess the purpose of the parameters.
    //----------------------------------------------------------------------------------------------------------
    
    public delegate void MovieLoader(string _filePath, int _iForceScreen, bool _bStoreState);
    public delegate void StatusBarUpdater(string _status);
    public delegate void TopMostMaker(Form _form);
    public delegate void ThumbnailsContainer(bool shortcuts, List<String> files, bool refresh);
    public delegate void FileExplorerRefresher(bool _bRefreshThumbnails);
    public delegate void FileExplorerTabChanger(ActiveFileBrowserTab tab);
    public delegate void ExplorerTabChanged(ActiveFileBrowserTab tab);
    
    public class DelegatesPool
    {
        public Action OpenVideoFile;
        public MovieLoader LoadMovieInScreen;
        public StatusBarUpdater UpdateStatusBar;
        public Action StopPlaying;
        public TopMostMaker MakeTopMost;
        public Action DeactivateKeyboardHandler;
        public Action ActivateKeyboardHandler;
        public ThumbnailsContainer CurrentDirectoryChanged;
        public FileExplorerRefresher RefreshFileExplorer;
        public FileExplorerTabChanger ChangeFileExplorerTab;
        public ExplorerTabChanged ExplorerTabChanged;
  
        private static DelegatesPool _instance = null;
        
        public static DelegatesPool Instance()
        {
            if (_instance == null)
                _instance = new DelegatesPool();

            return _instance;
        }
        
        private DelegatesPool()
        {
        }
    }
}
