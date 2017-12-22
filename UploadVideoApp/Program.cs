﻿using InstaSharper.API;
using InstaSharper.API.Builder;
using InstaSharper.Classes;
using InstaSharper.Classes.Models;
using System;
using System.Threading.Tasks;

namespace UploadVideoApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            UserSessionData userSession = new UserSessionData
            {
                UserName = "vasya.hardupkinovich",
                Password = "g4h56j"
            };

            IInstaApi instaApi = InstaApiBuilder.CreateBuilder()
                            .SetUser(userSession)
                            .SetRequestDelay(TimeSpan.FromSeconds(6.5)) // set delay between requests
                            .Build();

            var loggedIn = instaApi.LoginAsync().Result;
            if (!loggedIn.Succeeded)
            {
                Console.WriteLine("User not authenticated.Please check your instagram account");
                return;
            }

            var video = new InstaVideo("https://static.videezy.com/system/resources/previews/000/000/206/original/Clouds%20(time%20lapse)%20[SaveYouTube.com].mp4", 720, 720, 2);
            var thumbnail = new InstaImage("D:\\gold_car.jpg", 1080, 1080);
            var result = await instaApi.UploadTimelineVideoAsync(video, thumbnail, "cool video");

            //var video = new InstaVideo("https://static.videezy.com/system/resources/previews/000/000/206/original/Clouds%20(time%20lapse)%20[SaveYouTube.com].mp4", 600, 600, 2);
            //var thumbnail = new InstaImage("D:\\gold_car.jpg", 1080, 1080);
            //var result = await instaApi.UploadStoryVideoAsync(video, thumbnail, "cool video");
            //var image = new InstaImage("D:\\gold_car.jpg", 1080, 1080);
            //var result = await instaApi.UploadStoryPhotoAsync(image, "This is my car");
        }
    }
}