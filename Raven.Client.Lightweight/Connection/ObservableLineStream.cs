﻿// -----------------------------------------------------------------------
//  <copyright file="EventSourceStream.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Util;
using Raven.Database.Util;

namespace Raven.Client.Connection
{
	public class ObservableLineStream : IObservable<string>, IDisposable
	{
		private readonly Stream stream;
		private readonly byte[] buffer = new byte[8192];
		private int posInBuffer;
		private readonly Action onDispose;

		private readonly ConcurrentSet<IObserver<string>> subscribers = new ConcurrentSet<IObserver<string>>();

		public ObservableLineStream(Stream stream, Action onDispose)
		{
			this.stream = stream;
			this.onDispose = onDispose;
		}

		public void Start()
		{
			ReadAsync()
				.ContinueWith(task =>
				              	{
				              		var read = task.Result;
									if(read == 0)// will for a reopening of the connection
										throw new EndOfStreamException();
				              		// find \r\n in newly read range

				              		var startPos = 0;
				              		byte prev = 0;
				              		bool foundLines = false;
				              		for (int i = posInBuffer; i < posInBuffer + read; i++)
				              		{
				              			if (prev == '\r' && buffer[i] == '\n')
				              			{
				              				foundLines = true;
											// yeah, we found a line, let us give it to the users
											var data = Encoding.UTF8.GetString(buffer, startPos, i-1);
				              				startPos = i + 1;
				              				foreach (var subscriber in subscribers)
				              				{
				              					subscriber.OnNext(data);
				              				}
				              			}
				              			prev = buffer[i];
				              		}
				              		posInBuffer += read;
									if(startPos >= posInBuffer) // read to end
									{
										posInBuffer = 0;
										return;
									}
									if (foundLines == false)
										return;

									// move remaining to the start of buffer, then reset
				              		Array.Copy(buffer, startPos, buffer, 0, posInBuffer - startPos);
				              		posInBuffer -= startPos;
				              	})
								.ContinueWith(task =>
								{
									if(task.IsFaulted)
									{
										try
										{
											stream.Dispose();
										}
										catch (Exception)
										{
											// explicitly ignoring this
										}
										var aggregateException = task.Exception;
										if (aggregateException.ExtractSingleInnerException() is ObjectDisposedException)
											return; // this isn't an error
										foreach (var subscriber in subscribers)
										{
											subscriber.OnError(aggregateException);
										}
										return;
									}

									Start(); // read more lines
								});
		}


#if SILVERLIGHT
		private Task<int> ReadAsync()
		{
			try
			{
				return stream.ReadAsync(buffer, posInBuffer, buffer.Length - posInBuffer);
			}
			catch (Exception e)
			{
				return new CompletedTask<int>(e);
			}
		}
#else
		private Task<int> ReadAsync()
		{
			try
			{
				return Task.Factory.FromAsync<int>(
					(callback, state) => stream.BeginRead(buffer, posInBuffer, buffer.Length - posInBuffer, callback, state),
					stream.EndRead,
					null);
			}
			catch (Exception e)
			{
				return new CompletedTask<int>(e);
			}
		}
#endif

		public IDisposable Subscribe(IObserver<string> observer)
		{
			subscribers.TryAdd(observer);
			return new DisposableAction(() => subscribers.TryRemove(observer));
		}

		public void Dispose()
		{
			foreach (var subscriber in subscribers)
			{
				subscriber.OnCompleted();
			}

			onDispose();
		}
	}
}