// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.Its.Domain
{
    /// <summary>
    /// Represents that a command has been scheduled for future execution against a specific aggregate type.
    /// </summary>
    public interface IScheduledCommand<in TTarget> :
        //IEvent,
        IScheduledCommand
    {
        /// <summary>
        /// Gets the command to be applied at a later time.
        /// </summary>
        ICommand<TTarget> Command { get; }
    }
}
