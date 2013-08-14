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

namespace Kinovea.Services
{
    public interface ICommand
    {
    	/// <summary>
    	/// The name of the command as it will appear in the undo menu.
    	/// </summary>
        string FriendlyName
        {
            get;
        }

        /// <summary>
        /// Code to perform (or perform again in the case of redo) the command.
        /// </summary>
        void Execute();
    }
}